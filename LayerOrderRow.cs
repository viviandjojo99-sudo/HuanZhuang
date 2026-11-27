using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace DressUp.Actions
{
    /// <summary>
    /// 「图层顺序」页里的一行。长按拿起、拖动换位、松手放下。
    /// 没长按就拖 = 滚动列表（把事件转给 ScrollRect）。
    /// 位置、缩放、阴影都由 LayerOrderPad 每帧调用 Tick 驱动。
    /// </summary>
    public class LayerOrderRow : MonoBehaviour,
        IPointerDownHandler, IPointerUpHandler, IInitializePotentialDragHandler,
        IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        static readonly Color TextColor = new Color(0.20f, 0.20f, 0.26f, 1f);
        static readonly Color GripColor = new Color(0.55f, 0.55f, 0.62f, 1f);

        LayerOrderPad owner;
        ScrollRect scroll;
        Image background;
        Shadow shadow;
        Color baseColor;
        Color liftColor;

        bool pressed;
        bool scrolling;
        bool dragging;
        float pressTime;
        Vector2 pressPos;
        Camera pressCam;

        float velY;
        float scale = 1f;
        float scaleVel;

        public RectTransform Rect { get; private set; }
        public string Category { get; private set; }
        public List<string> Layers { get; private set; }
        public bool Lifted { get; private set; }
        public float TargetY { get; set; }

        public void Init(
            LayerOrderPad owner,
            ScrollRect scroll,
            string category,
            Sprite icon,
            List<string> layers,
            float height,
            float inset,
            Color baseColor,
            Color liftColor)
        {
            this.owner = owner;
            this.scroll = scroll;
            this.baseColor = baseColor;
            this.liftColor = liftColor;
            Category = category;
            Layers = layers;

            Rect = (RectTransform)transform;
            Rect.anchorMin = new Vector2(0f, 1f);
            Rect.anchorMax = new Vector2(1f, 1f);
            Rect.pivot = new Vector2(0.5f, 1f);
            Rect.sizeDelta = new Vector2(-2f * inset, height);
            Rect.anchoredPosition = Vector2.zero;
            Rect.localScale = Vector3.one;

            background = gameObject.AddComponent<Image>();
            background.color = baseColor;
            background.raycastTarget = true;

            shadow = gameObject.AddComponent<Shadow>();
            shadow.effectDistance = new Vector2(0f, -8f);
            shadow.effectColor = new Color(0f, 0f, 0f, 0f);
            shadow.enabled = false;

            float iconSize = Mathf.Max(0f, height - 24f);
            float textLeft = 24f;
            if (icon != null)
            {
                RectTransform iconRect = NewChild("Icon");
                iconRect.anchorMin = new Vector2(0f, 0.5f);
                iconRect.anchorMax = new Vector2(0f, 0.5f);
                iconRect.pivot = new Vector2(0f, 0.5f);
                iconRect.sizeDelta = new Vector2(iconSize, iconSize);
                iconRect.anchoredPosition = new Vector2(24f, 0f);
                Image iconImage = iconRect.gameObject.AddComponent<Image>();
                iconImage.sprite = icon;
                iconImage.preserveAspect = true;
                iconImage.raycastTarget = false;
                textLeft = 24f + iconSize + 20f;
            }

            RectTransform labelRect = NewChild("Label");
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(textLeft, 0f);
            labelRect.offsetMax = new Vector2(-96f, 0f);
            TextMeshProUGUI label = labelRect.gameObject.AddComponent<TextMeshProUGUI>();
            label.text = category;
            label.fontSize = 34f;
            label.color = TextColor;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.raycastTarget = false;

            RectTransform grip = NewChild("Grip");
            grip.anchorMin = new Vector2(1f, 0.5f);
            grip.anchorMax = new Vector2(1f, 0.5f);
            grip.pivot = new Vector2(1f, 0.5f);
            grip.sizeDelta = new Vector2(40f, 28f);
            grip.anchoredPosition = new Vector2(-28f, 0f);
            for (int i = 0; i < 3; i++)
            {
                RectTransform bar = new GameObject("Bar", typeof(RectTransform)).transform as RectTransform;
                bar.SetParent(grip, false);
                bar.anchorMin = new Vector2(0f, i * 0.5f);
                bar.anchorMax = new Vector2(1f, i * 0.5f);
                bar.pivot = new Vector2(0.5f, 0.5f);
                bar.sizeDelta = new Vector2(0f, 4f);
                bar.anchoredPosition = Vector2.zero;
                Image barImage = bar.gameObject.AddComponent<Image>();
                barImage.color = GripColor;
                barImage.raycastTarget = false;
            }
        }

        RectTransform NewChild(string childName)
        {
            var go = new GameObject(childName, typeof(RectTransform));
            go.transform.SetParent(transform, false);
            return (RectTransform)go.transform;
        }

        public void SetLifted(bool value)
        {
            Lifted = value;
            if (value)
                pressed = false;
        }

        /// <summary>每帧由 LayerOrderPad 调用：长按计时、平滑移动、拿起时的放大和阴影。</summary>
        public void Tick(float dt)
        {
            if (pressed && !Lifted && !scrolling
                && Time.unscaledTime - pressTime >= owner.LongPressSeconds)
            {
                owner.RequestLift(this, pressPos, pressCam);
            }

            float targetScale = Lifted ? owner.LiftedScale : 1f;
            scale = Mathf.SmoothDamp(scale, targetScale, ref scaleVel, 0.06f, Mathf.Infinity, dt);
            Rect.localScale = new Vector3(scale, scale, 1f);

            float t = Mathf.InverseLerp(1f, Mathf.Max(1.001f, owner.LiftedScale), scale);
            shadow.enabled = t > 0.02f;
            shadow.effectColor = new Color(0f, 0f, 0f, 0.35f * t);
            background.color = Color.Lerp(baseColor, liftColor, t);

            if (!Lifted)
            {
                Vector2 p = Rect.anchoredPosition;
                p.y = Mathf.SmoothDamp(p.y, TargetY, ref velY, owner.SettleTime, Mathf.Infinity, dt);
                Rect.anchoredPosition = p;
            }
            else
            {
                velY = 0f;
            }
        }

        /// <summary>不做动画，直接放到目标位置（刚生成时用）。</summary>
        public void SnapToTarget()
        {
            velY = 0f;
            Rect.anchoredPosition = new Vector2(0f, TargetY);
        }

        public void OnPointerDown(PointerEventData e)
        {
            if (e.button != PointerEventData.InputButton.Left)
                return;
            pressed = true;
            scrolling = false;
            dragging = false;
            pressTime = Time.unscaledTime;
            pressPos = e.position;
            pressCam = e.pressEventCamera;
        }

        public void OnPointerUp(PointerEventData e)
        {
            pressed = false;
            // 拿起后没拖就松手：原地放下
            if (Lifted && !dragging)
                owner.DropLifted(this);
        }

        public void OnInitializePotentialDrag(PointerEventData e)
        {
            if (scroll != null)
                ExecuteEvents.Execute(scroll.gameObject, e, ExecuteEvents.initializePotentialDrag);
        }

        public void OnBeginDrag(PointerEventData e)
        {
            dragging = true;
            if (Lifted)
                return;

            // 还没长按就开始拖：当作滚动列表，取消长按
            pressed = false;
            scrolling = true;
            if (scroll != null)
                ExecuteEvents.Execute(scroll.gameObject, e, ExecuteEvents.beginDragHandler);
        }

        public void OnDrag(PointerEventData e)
        {
            if (Lifted)
            {
                owner.DragLifted(e.position, e.pressEventCamera);
                return;
            }

            if (scrolling && scroll != null)
                ExecuteEvents.Execute(scroll.gameObject, e, ExecuteEvents.dragHandler);
        }

        public void OnEndDrag(PointerEventData e)
        {
            dragging = false;
            if (Lifted)
            {
                owner.DropLifted(this);
                return;
            }

            if (scrolling && scroll != null)
                ExecuteEvents.Execute(scroll.gameObject, e, ExecuteEvents.endDragHandler);
            scrolling = false;
        }

        void OnDisable()
        {
            // 拖到一半页面被关掉：按放下处理，不丢顺序
            if (Lifted && owner != null)
                owner.DropLifted(this);
            pressed = false;
            scrolling = false;
            dragging = false;
        }
    }
}
