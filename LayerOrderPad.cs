using System.Collections.Generic;
using DressUp.Audio;
using DressUp.Avatar;
using DressUp.Data;
using DressUp.Motion;
using DressUp.Wardrobe;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DressUp.Actions
{
    /// <summary>
    /// 挂在「图层顺序」按钮上。点按钮 → 打开 Pad 里的 IncreaseTag 页，
    /// 列出 TagA 里现在有的 Tag；长按拖动换位，松手后按列表顺序重排 BodyPanel 的图层。
    /// 列表越靠上，图层越在前面。
    ///
    /// 只重排「自己带图层」的一级 Tag（人模、眼睛、衣服、裤子……）。
    /// 头发、其它饰品这类只有二级 Tag（TagB/TagC）的，不进列表，它们的图层位置不动。
    /// 做法是：参与排序的图层只在「它们原本占着的层叠位置」之间互换，
    /// 所以夹在中间的头发/饰品图层始终待在原来的位置。
    /// </summary>
    [RequireComponent(typeof(Button))]
    public class LayerOrderPad : MonoBehaviour
    {
        /// <summary>Pad 下页面物体的名字。场景里的页面如果不叫这个名字，改这里。</summary>
        public const string PageName = "IncreaseTag";

        [Header("手感")]
        [SerializeField] float longPressSeconds = 0.35f;
        [SerializeField] float liftedScale = 1.06f;
        [Tooltip("其它行让位的平滑时间，越小越跟手。")]
        [SerializeField] float settleTime = 0.07f;
        [SerializeField] float edgeScrollZone = 90f;
        [SerializeField] float edgeScrollSpeed = 900f;

        [Header("外观")]
        [SerializeField] float rowHeight = 96f;
        [SerializeField] float rowSpacing = 12f;
        [SerializeField] float rowInset = 8f;
        [SerializeField] Color rowColor = new Color(1f, 1f, 1f, 0.94f);
        [SerializeField] Color rowLiftColor = new Color(1f, 0.97f, 0.88f, 1f);

        [Header("文字（页面自动生成时用）")]
        [SerializeField] string title = "图层顺序";
        [SerializeField] string hint = "长按拖动 · 越靠上越在前面";
        [SerializeField] string resetLabel = "恢复默认";
        [SerializeField] string emptyText = "没有可以调整的图层";

        Button openButton;
        RectTransform page;
        ScrollRect scroll;
        RectTransform content;
        TextMeshProUGUI emptyLabel;

        BodyAvatar avatar;
        readonly List<LayerOrderRow> order = new List<LayerOrderRow>();
        LayerOrderRow lifted;
        float grabOffset;
        Vector2 lastScreenPos;
        Camera lastCam;
        string committedSignature = "";

        public float LongPressSeconds => longPressSeconds;
        public float LiftedScale => liftedScale;
        public float SettleTime => settleTime;

        float Step => rowHeight + rowSpacing;
        float TopPad => rowSpacing;

        void Awake()
        {
            openButton = GetComponent<Button>();
            openButton.onClick.AddListener(Open);
        }

        void OnDestroy()
        {
            if (openButton != null)
                openButton.onClick.RemoveListener(Open);
        }

        // ───────── 打开 / 重建 ─────────

        public void Open()
        {
            PadHost host = PadHost.Wire();
            if (host == null)
            {
                Debug.LogWarning("[LayerOrderPad] 场景里找不到 Pad。", this);
                return;
            }

            EnsurePage(host.transform);
            if (!Rebuild())
                Debug.LogWarning("[LayerOrderPad] 找不到当前角色或衣柜，列表为空。", this);

            PadHost.Show(PageName);
            DressUpAudio.Instance?.PlayKey();
        }

        bool Rebuild()
        {
            ClearRows();
            lifted = null;
            if (content == null)
                return false;

            var binder = FindAnyObjectByType<ActiveAvatarBinder>();
            avatar = binder != null ? binder.ActiveAvatar : FindAnyObjectByType<BodyAvatar>();
            ClothingCatalog catalog = binder != null ? binder.ActiveCatalog : null;

            bool ok = avatar != null && catalog != null;
            var data = new List<RowData>();
            if (ok)
                CollectRows(catalog, data);

            // 列表自上而下 = 层叠自前而后，所以按「最高层的位置」从大到小排
            data.Sort((a, b) => b.z.CompareTo(a.z));

            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);

            for (int i = 0; i < data.Count; i++)
            {
                var go = new GameObject(data[i].category, typeof(RectTransform));
                go.transform.SetParent(content, false);
                var row = go.AddComponent<LayerOrderRow>();
                row.Init(this, scroll, data[i].category, data[i].icon, data[i].layers,
                    rowHeight, rowInset, rowColor, rowLiftColor);
                order.Add(row);
            }

            LayoutTargets();
            for (int i = 0; i < order.Count; i++)
                order[i].SnapToTarget();

            content.anchoredPosition = Vector2.zero;
            if (scroll != null)
                scroll.StopMovement();
            if (emptyLabel != null)
                emptyLabel.gameObject.SetActive(order.Count == 0);

            committedSignature = BuildSignature();
            return ok;
        }

        struct RowData
        {
            public string category;
            public Sprite icon;
            public List<string> layers;
            public int z;
        }

        void CollectRows(ClothingCatalog catalog, List<RowData> result)
        {
            var current = new List<string>();
            avatar.GetLayerOrder(current);

            var used = new HashSet<string>(System.StringComparer.Ordinal);
            Transform column = FindTagColumn();
            if (column == null)
                return;

            WardrobeTag[] tags = column.GetComponentsInChildren<WardrobeTag>(true);
            for (int t = 0; t < tags.Length; t++)
            {
                WardrobeTag tag = tags[t];
                if (tag == null || !tag.gameObject.activeSelf)
                    continue;

                string category = tag.Category;
                if (string.IsNullOrEmpty(category)
                    || category == WardrobeTagNavigator.BackCategory
                    || category == "返回")
                    continue;

                WardrobeTagDef def = catalog.FindTag(category);
                if (def == null || def.layers == null)
                    continue;

                var layers = new List<string>();
                for (int i = 0; i < def.layers.Count; i++)
                {
                    string layer = def.layers[i];
                    if (!string.IsNullOrEmpty(layer) && current.Contains(layer) && used.Add(layer))
                        layers.Add(layer);
                }

                // 头发、其它饰品自己没有图层（图层都在二级 Tag 下），不列出
                if (layers.Count == 0)
                    continue;

                layers.Sort((a, b) => current.IndexOf(a).CompareTo(current.IndexOf(b)));
                result.Add(new RowData
                {
                    category = category,
                    icon = IconOf(tag),
                    layers = layers,
                    z = current.IndexOf(layers[layers.Count - 1])
                });
            }
        }

        static Sprite IconOf(WardrobeTag tag)
        {
            Transform icon = tag.transform.Find("Icon");
            Image image = icon != null ? icon.GetComponent<Image>() : null;
            return image != null ? image.sprite : null;
        }

        static Transform FindTagColumn()
        {
            Transform[] all = FindObjectsByType<Transform>(FindObjectsInactive.Include);
            Transform fallback = null;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null)
                    continue;
                if (all[i].name == "TagA")
                    return all[i];
                if (all[i].name == "Tag1" && fallback == null)
                    fallback = all[i];
            }

            return fallback;
        }

        void ClearRows()
        {
            for (int i = 0; i < order.Count; i++)
            {
                if (order[i] != null)
                    Destroy(order[i].gameObject);
            }

            order.Clear();
        }

        // ───────── 排布 ─────────

        void LayoutTargets()
        {
            for (int i = 0; i < order.Count; i++)
                order[i].TargetY = -(TopPad + i * Step);

            if (content != null)
            {
                float height = order.Count == 0 ? 0f : TopPad * 2f + order.Count * Step - rowSpacing;
                content.sizeDelta = new Vector2(0f, height);
            }
        }

        // ───────── 拿起 / 拖动 / 放下（由 LayerOrderRow 调用）─────────

        public bool RequestLift(LayerOrderRow row, Vector2 screenPos, Camera cam)
        {
            if (lifted != null || row == null || content == null)
                return false;

            lastScreenPos = screenPos;
            lastCam = cam;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(content, screenPos, cam, out Vector2 local))
                return false;

            lifted = row;
            grabOffset = row.Rect.anchoredPosition.y - local.y;
            row.SetLifted(true);
            row.Rect.SetAsLastSibling();
            if (scroll != null)
                scroll.StopMovement();
            return true;
        }

        public void DragLifted(Vector2 screenPos, Camera cam)
        {
            lastScreenPos = screenPos;
            lastCam = cam;
        }

        public void DropLifted(LayerOrderRow row)
        {
            if (row == null || row != lifted)
                return;

            lifted = null;
            row.SetLifted(false);
            LayoutTargets();
            for (int i = 0; i < order.Count; i++)
            {
                if (order[i] != null)
                    order[i].Rect.SetSiblingIndex(i);
            }

            Commit();
        }

        void Update()
        {
            if (page == null || !page.gameObject.activeInHierarchy)
                return;

            float dt = Time.unscaledDeltaTime;
            if (lifted != null)
            {
                AutoScroll(dt);
                FollowPointer();
            }

            for (int i = 0; i < order.Count; i++)
            {
                if (order[i] != null)
                    order[i].Tick(dt);
            }
        }

        void FollowPointer()
        {
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(content, lastScreenPos, lastCam, out Vector2 local))
                return;

            float top = local.y + grabOffset;
            float highest = -TopPad;
            float lowest = -(TopPad + (order.Count - 1) * Step);
            top = Mathf.Clamp(top, lowest - Step * 0.3f, highest + Step * 0.3f);
            lifted.Rect.anchoredPosition = new Vector2(0f, top);

            float centerY = top - rowHeight * 0.5f;
            int slot = Mathf.FloorToInt((-centerY - TopPad + rowSpacing * 0.5f) / Step);
            slot = Mathf.Clamp(slot, 0, order.Count - 1);

            int index = order.IndexOf(lifted);
            if (slot != index)
            {
                order.RemoveAt(index);
                order.Insert(slot, lifted);
                LayoutTargets();
            }
        }

        void AutoScroll(float dt)
        {
            if (scroll == null)
                return;

            RectTransform viewport = scroll.viewport != null ? scroll.viewport : (RectTransform)scroll.transform;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(viewport, lastScreenPos, lastCam, out Vector2 local))
                return;

            float maxScroll = Mathf.Max(0f, content.rect.height - viewport.rect.height);
            if (maxScroll <= 0f)
                return;

            float fromTop = viewport.rect.yMax - local.y;
            float fromBottom = local.y - viewport.rect.yMin;
            float y = content.anchoredPosition.y;
            if (fromTop < edgeScrollZone)
                y -= edgeScrollSpeed * Mathf.Clamp01((edgeScrollZone - fromTop) / edgeScrollZone) * dt;
            else if (fromBottom < edgeScrollZone)
                y += edgeScrollSpeed * Mathf.Clamp01((edgeScrollZone - fromBottom) / edgeScrollZone) * dt;

            content.anchoredPosition = new Vector2(content.anchoredPosition.x, Mathf.Clamp(y, 0f, maxScroll));
            scroll.velocity = Vector2.zero;
        }

        // ───────── 应用到角色 ─────────

        string BuildSignature()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < order.Count; i++)
            {
                if (i > 0)
                    sb.Append('|');
                sb.Append(order[i].Category);
            }

            return sb.ToString();
        }

        void Commit()
        {
            if (avatar == null)
                return;

            string signature = BuildSignature();
            if (signature == committedSignature)
                return;

            // 列表最下面一行在最底，所以从后往前展开
            var bottomToTop = new List<string>();
            for (int i = order.Count - 1; i >= 0; i--)
                bottomToTop.AddRange(order[i].Layers);

            if (avatar.ApplyLayerOrder(bottomToTop))
            {
                committedSignature = signature;
                DressUpAudio.Instance?.PlayKey();
            }
        }

        public void ResetToDefault()
        {
            if (avatar == null)
                return;
            avatar.ResetLayerOrder();
            Rebuild();
        }

        // ───────── 页面 ─────────

        void EnsurePage(Transform pad)
        {
            Transform existing = pad.Find(PageName);
            if (existing == null)
            {
                RectTransform created = NewRect(PageName, pad);
                Transform sibling = pad.Find("RandomSettings") ?? pad.Find("SaveMaleta") ?? pad.Find("InputPad");
                if (sibling is RectTransform s)
                {
                    created.anchorMin = s.anchorMin;
                    created.anchorMax = s.anchorMax;
                    created.pivot = s.pivot;
                    created.sizeDelta = s.sizeDelta;
                    created.anchoredPosition = s.anchoredPosition;
                }
                else
                {
                    Stretch(created, 40f, 40f, 40f, 40f);
                }

                created.gameObject.SetActive(false);
                existing = created;
            }

            page = (RectTransform)existing;
            scroll = page.GetComponentInChildren<ScrollRect>(true);
            if (scroll == null)
                BuildDefaultUi();

            content = scroll.content;
            if (content == null)
            {
                Debug.LogWarning("[LayerOrderPad] 页面里的 ScrollRect 没有指定 Content。", this);
                return;
            }

            // 页面里自己做了「恢复默认」按钮（名字 ResetButton）就接上
            Transform reset = page.Find("ResetButton");
            Button resetButton = reset != null ? reset.GetComponent<Button>() : null;
            if (resetButton != null)
            {
                resetButton.onClick.RemoveListener(ResetToDefault);
                resetButton.onClick.AddListener(ResetToDefault);
            }
        }

        void BuildDefaultUi()
        {
            TextMeshProUGUI titleText = NewText("Title", page, title, 44f, Color.white, TextAlignmentOptions.Center);
            titleText.rectTransform.anchorMin = new Vector2(0f, 1f);
            titleText.rectTransform.anchorMax = new Vector2(1f, 1f);
            titleText.rectTransform.pivot = new Vector2(0.5f, 1f);
            titleText.rectTransform.sizeDelta = new Vector2(0f, 90f);
            titleText.rectTransform.anchoredPosition = Vector2.zero;

            TextMeshProUGUI hintText = NewText("Hint", page, hint, 26f, new Color(1f, 1f, 1f, 0.75f), TextAlignmentOptions.Center);
            hintText.rectTransform.anchorMin = new Vector2(0f, 1f);
            hintText.rectTransform.anchorMax = new Vector2(1f, 1f);
            hintText.rectTransform.pivot = new Vector2(0.5f, 1f);
            hintText.rectTransform.sizeDelta = new Vector2(0f, 50f);
            hintText.rectTransform.anchoredPosition = new Vector2(0f, -90f);

            RectTransform list = NewRect("List", page);
            Stretch(list, 24f, 110f, 24f, 150f);
            Image catcher = list.gameObject.AddComponent<Image>();
            catcher.color = new Color(0f, 0f, 0f, 0f);
            scroll = list.gameObject.AddComponent<ScrollRect>();

            RectTransform viewport = NewRect("Viewport", list);
            Stretch(viewport, 0f, 0f, 0f, 0f);
            viewport.gameObject.AddComponent<RectMask2D>();

            RectTransform body = NewRect("Content", viewport);
            body.anchorMin = new Vector2(0f, 1f);
            body.anchorMax = new Vector2(1f, 1f);
            body.pivot = new Vector2(0.5f, 1f);
            body.sizeDelta = Vector2.zero;

            scroll.viewport = viewport;
            scroll.content = body;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Elastic;
            scroll.elasticity = 0.1f;
            scroll.inertia = true;
            scroll.decelerationRate = 0.135f;
            scroll.scrollSensitivity = 40f;

            emptyLabel = NewText("Empty", page, emptyText, 30f, new Color(1f, 1f, 1f, 0.7f), TextAlignmentOptions.Center);
            Stretch(emptyLabel.rectTransform, 24f, 110f, 24f, 150f);
            emptyLabel.gameObject.SetActive(false);

            RectTransform resetRect = NewRect("ResetButton", page);
            resetRect.anchorMin = new Vector2(0.5f, 0f);
            resetRect.anchorMax = new Vector2(0.5f, 0f);
            resetRect.pivot = new Vector2(0.5f, 0f);
            resetRect.sizeDelta = new Vector2(320f, 80f);
            resetRect.anchoredPosition = new Vector2(0f, 20f);
            Image resetImage = resetRect.gameObject.AddComponent<Image>();
            resetImage.color = new Color(1f, 1f, 1f, 0.92f);
            Button resetButton = resetRect.gameObject.AddComponent<Button>();
            resetButton.targetGraphic = resetImage;
            if (resetRect.GetComponent<UiButtonPressMotion>() == null)
                resetRect.gameObject.AddComponent<UiButtonPressMotion>();
            TextMeshProUGUI resetText = NewText("Label", resetRect, resetLabel, 32f,
                new Color(0.20f, 0.20f, 0.26f, 1f), TextAlignmentOptions.Center);
            Stretch(resetText.rectTransform, 0f, 0f, 0f, 0f);
        }

        static RectTransform NewRect(string objectName, Transform parent)
        {
            var go = new GameObject(objectName, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        static TextMeshProUGUI NewText(string objectName, Transform parent, string text, float size, Color color, TextAlignmentOptions align)
        {
            RectTransform rect = NewRect(objectName, parent);
            TextMeshProUGUI label = rect.gameObject.AddComponent<TextMeshProUGUI>();
            label.text = text;
            label.fontSize = size;
            label.color = color;
            label.alignment = align;
            label.raycastTarget = false;
            return label;
        }

        static void Stretch(RectTransform rect, float left, float bottom, float right, float top)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(-right, -top);
        }
    }
}
