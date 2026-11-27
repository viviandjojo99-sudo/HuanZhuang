using System;
using DressUp.Audio;
using UnityEngine;
using UnityEngine.UI;

namespace DressUp.Actions
{
    /// <summary>
    /// Pad 底图和关闭按钮。随机设置、导入、储存衣柜同时只开一个。
    /// 打开时在 Pad 后面盖一层暗色，挡住后面的点击。只有关闭按钮会关掉。
    /// </summary>
    public class PadHost : MonoBehaviour
    {
        static readonly string[] Pages = { "RandomSettings", "InputPad", "SaveMaleta", LayerOrderPad.PageName };

        GameObject dimmer;
        bool prepared;

        public static PadHost Current { get; private set; }

        public static event Action Closed;

        public static PadHost Wire()
        {
            Transform pad = FindNamed("Pad");
            if (pad == null)
                return null;

            PadHost host = pad.GetComponent<PadHost>();
            if (host == null)
                host = pad.gameObject.AddComponent<PadHost>();
            host.Prepare();
            return host;
        }

        public static void Show(string pageName)
        {
            PadHost host = Wire();
            if (host == null)
                return;
            host.ShowPage(pageName);
        }

        public static void Close()
        {
            if (Current != null)
                Current.Hide();
        }

        void Prepare()
        {
            Current = this;
            EnsureDimmer();
            Transform close = transform.Find("CloseButton");
            if (close != null)
            {
                Button button = close.GetComponent<Button>();
                if (button != null)
                {
                    button.onClick.RemoveListener(Hide);
                    button.onClick.AddListener(Hide);
                }
            }

            if (prepared)
                return;

            prepared = true;
            SetPages(null);
            if (dimmer != null)
                dimmer.SetActive(false);
            gameObject.SetActive(false);
        }

        void OnDestroy()
        {
            if (Current == this)
                Current = null;
        }

        void ShowPage(string pageName)
        {
            SetPages(pageName);
            EnsureDimmer();
            if (dimmer != null)
            {
                dimmer.SetActive(true);
                dimmer.transform.SetAsLastSibling();
            }

            gameObject.SetActive(true);
            transform.SetAsLastSibling();
            Transform close = transform.Find("CloseButton");
            if (close != null)
                close.SetAsLastSibling();
        }

        void Hide()
        {
            DressUpAudio.Instance?.PlayKey();
            gameObject.SetActive(false);
            if (dimmer != null)
                dimmer.SetActive(false);
            Closed?.Invoke();
        }

        void SetPages(string pageName)
        {
            for (int i = 0; i < Pages.Length; i++)
            {
                Transform page = transform.Find(Pages[i]);
                if (page == null)
                    continue;
                page.gameObject.SetActive(pageName != null && page.name == pageName);
            }
        }

        void EnsureDimmer()
        {
            if (dimmer != null || transform.parent == null)
                return;

            dimmer = new GameObject("PadDim", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            dimmer.transform.SetParent(transform.parent, false);
            RectTransform rect = dimmer.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.localScale = Vector3.one;
            Image image = dimmer.GetComponent<Image>();
            image.color = new Color(0f, 0f, 0f, 0.65f);
            image.raycastTarget = true;
            dimmer.SetActive(false);
        }

        static Transform FindNamed(string objectName)
        {
            Transform[] all = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].name == objectName)
                    return all[i];
            }

            return null;
        }
    }
}
