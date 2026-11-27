using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using DressUp.Data;
using DressUp.Import;
using DressUp.Tinting;
using UnityEngine;
using UnityEngine.UI;

namespace DressUp.Avatar
{
    /// <summary>
    /// 挂在任意 BodyPanel。衣服、裤子、鞋子、外套、袜子、嘴、眼睛、头发各层一件。
    /// 饰品层最多同时两件，第三件换掉最早的那件。随机先清空再各穿一件。
    /// 图层下可有 B/B1/B2… 描边与 S/S1/S2… 填色；调色只改填色。
    /// </summary>
    public class BodyAvatar : MonoBehaviour
    {
        static readonly Regex OutlineName = new Regex(@"^B\d*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex FillName = new Regex(@"^S\d*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        [SerializeField] string characterId = "Default";

        [Tooltip("Clear 时不清空的图层名")]
        [SerializeField] List<string> keepOnClear = new List<string>();

        [Tooltip("留空 = 所有子 Image 都可换装")]
        [SerializeField] List<string> swappableLayers = new List<string>();

        [Tooltip("刘海空缺的内置补图。穿上刘海时叠在第二张填色上，并跟刘海同色")]
        [SerializeField] Sprite lhS2;

        public const string BackSlotSuffix = "_back";
        public const int AccessoryWearCapacity = 2;

        static readonly HashSet<string> AccessoryWearLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "BS", "TSQ", "TSH", "DS", "MS", "YS", "ED", "WB", "SE", "J", "CJ", "GG"
        };

        /// <summary>曲线变色时头发线稿也跟一点填色，避免模板线稿原色太跳。</summary>
        static readonly HashSet<string> HairCurveOutlineLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DM", "BF", "LH", "CF", "HF", "HFBZ", "SF", "BZ"
        };
        const float HairCurveOutlineMix = 0.62f;

        readonly Dictionary<string, LayerSlot> slots = new Dictionary<string, LayerSlot>(StringComparer.Ordinal);

        // 场景里原本的层叠顺序（自下而上），「恢复默认」用。
        List<string> defaultLayerOrder;
        bool layerOrderCustomized;
        readonly Dictionary<string, EquippedSlot> wearByLayer = new Dictionary<string, EquippedSlot>(StringComparer.Ordinal);
        readonly Dictionary<string, List<EquippedSlot>> wearOrder = new Dictionary<string, List<EquippedSlot>>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, LayerSlot> backSlots = new Dictionary<string, LayerSlot>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Color> chosenColors = new Dictionary<string, Color>(StringComparer.Ordinal);
        readonly Dictionary<string, float> chosenLifts = new Dictionary<string, float>(StringComparer.Ordinal);
        readonly Dictionary<string, string> companionOwner = new Dictionary<string, string>(StringComparer.Ordinal);

        int outfitBatch;
        bool plainEquip;

        public string CharacterId => characterId;
        public bool IsOutfitBatching => outfitBatch > 0;
        public event Action<BodyAvatar, ClothingItem> Equipped;

        /// <summary>
        /// 随机换装批量编辑：合并存档脏标记；plainEquip 时先素色穿上（不开曲线），避免穿完再整身曲线重涂。
        /// </summary>
        public void BeginOutfitBatch(bool plainEquipColors = false)
        {
            outfitBatch++;
            if (plainEquipColors)
                plainEquip = true;
        }

        public void EndOutfitBatch()
        {
            if (outfitBatch <= 0)
                return;
            outfitBatch--;
            if (outfitBatch > 0)
                return;
            plainEquip = false;
            ImportedSave.NoteOutfitChanged();
        }

        void NoteOutfitIfNeeded()
        {
            if (outfitBatch <= 0)
                ImportedSave.NoteOutfitChanged();
        }

        void Awake()
        {
            RebuildLayerCache();
            if (defaultLayerOrder == null)
            {
                defaultLayerOrder = new List<string>();
                GetLayerOrder(defaultLayerOrder);
            }
            if (GetComponent<BodyAvatarWearPop>() == null)
                gameObject.AddComponent<BodyAvatarWearPop>();
        }

        public void RebuildLayerCache()
        {
            slots.Clear();
            for (int i = 0; i < transform.childCount; i++)
            {
                Transform child = transform.GetChild(i);
                if (child.name.EndsWith(BackSlotSuffix, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (slots.ContainsKey(child.name))
                {
                    Debug.LogWarning($"[BodyAvatar] 重复图层名 '{child.name}'", this);
                    continue;
                }

                var slot = LayerSlot.Build(child);
                if (slot == null)
                    continue;
                slots[child.name] = slot;
            }
        }

        public bool LayerOrderCustomized => layerOrderCustomized;

        public bool HasLayer(string layerName)
        {
            return !string.IsNullOrEmpty(layerName) && slots.ContainsKey(layerName);
        }

        /// <summary>现在的层叠顺序：前件图层名，自下而上（Hierarchy 顺序）。_back 不列出。</summary>
        public void GetLayerOrder(List<string> result)
        {
            if (result == null)
                return;
            result.Clear();
            for (int i = 0; i < transform.childCount; i++)
            {
                string childName = transform.GetChild(i).name;
                if (childName.EndsWith(BackSlotSuffix, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (slots.ContainsKey(childName))
                    result.Add(childName);
            }
        }

        /// <summary>
        /// 按 bottomToTop 重排图层。只动列表里的图层：它们在原本占着的层叠位置之间互换，
        /// 不在列表里的图层（头发、饰品等）下标不变，所以会夹在原来的位置。
        /// 双饰品的 _back 紧跟在自己的前件后面。
        /// </summary>
        public bool ApplyLayerOrder(IList<string> bottomToTop, bool notifySave = true, bool markCustom = true)
        {
            if (bottomToTop == null || bottomToTop.Count < 2)
                return false;

            var wanted = new List<LayerSlot>(bottomToTop.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < bottomToTop.Count; i++)
            {
                string name = bottomToTop[i];
                if (string.IsNullOrEmpty(name) || !seen.Add(name))
                    continue;
                if (slots.TryGetValue(name, out LayerSlot slot) && slot != null
                    && slot.Root != null && slot.Root.parent == transform)
                    wanted.Add(slot);
            }

            if (wanted.Count < 2)
                return false;

            var positions = new List<int>(wanted.Count);
            for (int i = 0; i < wanted.Count; i++)
                positions.Add(wanted[i].Root.GetSiblingIndex());
            positions.Sort();

            var children = new List<Transform>(transform.childCount);
            for (int i = 0; i < transform.childCount; i++)
                children.Add(transform.GetChild(i));
            for (int i = 0; i < wanted.Count; i++)
                children[positions[i]] = wanted[i].Root;
            for (int i = 0; i < children.Count; i++)
                children[i].SetSiblingIndex(i);

            // _back 放回自己前件的正后面
            foreach (KeyValuePair<string, LayerSlot> pair in backSlots)
            {
                if (pair.Value == null || pair.Value.Root == null)
                    continue;
                if (slots.TryGetValue(pair.Key, out LayerSlot front) && front != null && front.Root != null)
                    pair.Value.Root.SetSiblingIndex(front.Root.GetSiblingIndex());
            }

            if (markCustom)
                layerOrderCustomized = true;
            if (notifySave)
                NoteOutfitIfNeeded();
            return true;
        }

        /// <summary>恢复场景里原本的层叠顺序。</summary>
        public void ResetLayerOrder(bool notifySave = true)
        {
            if (defaultLayerOrder != null)
                ApplyLayerOrder(defaultLayerOrder, notifySave: false, markCustom: false);
            layerOrderCustomized = false;
            if (notifySave)
                NoteOutfitIfNeeded();
        }

        /// <summary>返回该层用于调色的主填色 Image（S / S1，或旧单 Image）。</summary>
        public bool TryGetLayer(string layerName, out Image image)
        {
            image = null;
            if (!slots.TryGetValue(layerName, out LayerSlot slot))
                return false;
            image = slot.PrimaryFill;
            return image != null;
        }

        /// <summary>
        /// 收集该层「前件」已显示零件（S1/S2/B 等），供穿装弹动。
        /// 双饰品时旧件在 _back，不弹动，只弹新穿上的前件。
        /// </summary>
        public int CollectActivePartRects(string layerName, List<RectTransform> buffer)
        {
            if (buffer == null)
                return 0;
            buffer.Clear();
            if (!slots.TryGetValue(layerName, out LayerSlot slot) || slot == null)
                return 0;

            void AddIfActive(Image img)
            {
                if (img != null && img.enabled && img.sprite != null && img.rectTransform != null)
                    buffer.Add(img.rectTransform);
            }

            for (int i = 0; i < slot.Fills.Count; i++)
                AddIfActive(slot.Fills[i]);
            for (int i = 0; i < slot.Outlines.Count; i++)
                AddIfActive(slot.Outlines[i]);
            if (slot.Fills.Count == 0)
                AddIfActive(slot.Legacy);

            return buffer.Count;
        }

        /// <summary>当前穿着的前件图层名（含联动层），供随机换装结束后统一 DUANG。</summary>
        public int CollectWornFrontLayers(List<string> into)
        {
            if (into == null)
                return 0;
            into.Clear();
            foreach (KeyValuePair<string, EquippedSlot> pair in wearByLayer)
            {
                if (pair.Value == null || string.IsNullOrEmpty(pair.Key))
                    continue;
                into.Add(pair.Key);
            }

            return into.Count;
        }

        /// <summary>图层根 Rect（DUANG 只缩这一层，S/B 子件跟着走，避免各自缩放撕缝）。</summary>
        public bool TryGetLayerRootRect(string layerName, out RectTransform rect)
        {
            rect = null;
            if (!slots.TryGetValue(layerName, out LayerSlot slot) || slot?.Root == null)
                return false;
            rect = slot.Root as RectTransform;
            if (rect == null)
                rect = slot.Root.GetComponent<RectTransform>();
            return rect != null;
        }

        /// <summary>把该层 S/B 子件 scale 拉回 1（旧版曾对子件单独 DUANG，可能残留缩放）。</summary>
        public void ResetLayerPartScales(string layerName)
        {
            if (!slots.TryGetValue(layerName, out LayerSlot slot) || slot == null)
                return;

            void Reset(Image img)
            {
                if (img != null && img.rectTransform != null)
                    img.rectTransform.localScale = Vector3.one;
            }

            for (int i = 0; i < slot.Fills.Count; i++)
                Reset(slot.Fills[i]);
            for (int i = 0; i < slot.Outlines.Count; i++)
                Reset(slot.Outlines[i]);
            Reset(slot.Legacy);
        }

        /// <summary>只给未随机上色、但仍带色的层补曲线材质（比整身 RefreshTintStyle 轻）。</summary>
        public void RefreshCurveOnWornExcept(HashSet<string> skipLayers)
        {
            if (!TintStyle.Curve && !TintStyle.TwoColor)
                return;

            foreach (KeyValuePair<string, EquippedSlot> pair in wearByLayer)
            {
                if (pair.Value == null || string.IsNullOrEmpty(pair.Key))
                    continue;
                if (skipLayers != null && skipLayers.Contains(pair.Key))
                    continue;
                if (!slots.TryGetValue(pair.Key, out LayerSlot slot) || slot == null)
                    continue;
                Image fill = slot.PrimaryFill;
                if (fill == null)
                    continue;
                slot.SetFillColor(fill.color, SpriteBrighten.Get(fill), null, null, false);
            }
        }

        public static bool AllowsTwo(string layerName)
        {
            return !string.IsNullOrEmpty(layerName) && AccessoryWearLayers.Contains(layerName);
        }

        public void CollectWornItemIds(string layerName, List<string> into)
        {
            if (into == null || string.IsNullOrEmpty(layerName))
                return;

            if (wearOrder.TryGetValue(layerName, out List<EquippedSlot> worn) && worn != null)
            {
                for (int i = 0; i < worn.Count; i++)
                {
                    if (worn[i] != null && !string.IsNullOrEmpty(worn[i].itemId))
                        into.Add(worn[i].itemId);
                }

                return;
            }

            if (wearByLayer.TryGetValue(layerName, out EquippedSlot slot) && slot != null && !string.IsNullOrEmpty(slot.itemId))
                into.Add(slot.itemId);
        }

        public bool IsWearing(ClothingItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.layerName))
                return false;

            if (wearOrder.TryGetValue(item.layerName, out List<EquippedSlot> worn) && worn != null)
            {
                for (int i = 0; i < worn.Count; i++)
                {
                    if (worn[i] != null && worn[i].itemId == item.itemId)
                        return true;
                }

                return false;
            }

            return wearByLayer.TryGetValue(item.layerName, out EquippedSlot slot)
                   && slot != null
                   && slot.itemId == item.itemId;
        }

        public bool ToggleEquip(ClothingItem item)
        {
            if (item == null)
                return false;

            if (IsWearing(item))
            {
                if (AllowsTwo(item.layerName))
                    RemoveOne(item);
                else
                    UnequipLayer(item.layerName);
                return false;
            }

            return Equip(item);
        }

        public void UnequipLayer(string layerName)
        {
            if (string.IsNullOrEmpty(layerName))
                return;
            ClearLayers(new[] { layerName });
            Equipped?.Invoke(this, null);
            ImportedSave.NoteOutfitChanged();
        }

        public bool Equip(ClothingItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.layerName))
                return false;

            ImportedSave.EnsureResident(item);
            if (!CanSwap(item.layerName))
                return false;

            if (!slots.TryGetValue(item.layerName, out LayerSlot slot))
            {
                if (item.layerName == "QJ")
                    slot = CreateOverflowLayer(item.layerName);
                if (slot == null)
                {
                    Debug.LogWarning($"[BodyAvatar] 找不到图层 '{item.layerName}'", this);
                    return false;
                }
            }

            List<Sprite> fills = item.CollectFillSprites();
            if (fills.Count == 0)
            {
                Debug.LogWarning($"[BodyAvatar] 「{item.itemId}」没有 sprite", this);
                return false;
            }

            if (IsTemplateBangs(item))
                AppendLhPatch(fills);

            Color equipColor = ColorForEquip(item);
            float equipLift = LiftForEquip(item);
            if (AllowsTwo(item.layerName))
            {
                if (IsWearing(item))
                    return true;
                PushAccessory(item, slot, fills, equipColor, equipLift);
            }
            else
            {
                DestroyBack(item.layerName);
                PaintSlot(slot, item, fills, equipColor, equipLift);
                var worn = new EquippedSlot
                {
                    layerName = item.layerName,
                    itemId = item.itemId
                };
                wearByLayer[item.layerName] = worn;
                wearOrder[item.layerName] = new List<EquippedSlot> { worn };
            }

            ApplyCompanion(item);

            // 随机批量换装时不逐件弹 WearPop，否则头发+服装会卡一下。
            if (outfitBatch <= 0)
                Equipped?.Invoke(this, item);
            NoteOutfitIfNeeded();
            return true;
        }

        void PaintSlot(LayerSlot slot, ClothingItem item, List<Sprite> fills, Color equipColor, float equipLift)
        {
            List<Sprite> outlines = item.CollectOutlineSprites();
            // 换了新衣服：旧衣服留下的 B 色、渐变种类、缓存都不能带过来，
            // 否则新增的图层部件会因为缓存相同而被跳过，没有渐变材质。
            slot.ResetTintCache();
            slot.EnsureParts(outlines.Count, fills.Count);
            slot.ApplyOutlines(outlines, equipColor);
            slot.ApplyFills(fills, equipColor);
            // 批量素穿：只铺色，曲线材质留给随后的 ApplyChecked / RefreshTintStyle 一次做完。
            slot.SetFillColor(equipColor, equipLift, plainEquip ? false : (bool?)null);
        }

        void PushAccessory(ClothingItem item, LayerSlot slot, List<Sprite> fills, Color equipColor, float equipLift)
        {
            if (!wearOrder.TryGetValue(item.layerName, out List<EquippedSlot> list) || list == null)
            {
                list = new List<EquippedSlot>();
                wearOrder[item.layerName] = list;
            }

            if (list.Count >= AccessoryWearCapacity)
                DropOldest(item.layerName, list);

            if (list.Count == 1)
                CloneBehind(item.layerName, slot);

            PaintSlot(slot, item, fills, equipColor, equipLift);
            var worn = new EquippedSlot
            {
                layerName = item.layerName,
                itemId = item.itemId
            };
            list.Add(worn);
            wearByLayer[item.layerName] = worn;
        }

        void DropOldest(string layerName, List<EquippedSlot> list)
        {
            if (list == null || list.Count == 0)
                return;
            list.RemoveAt(0);
            DestroyBack(layerName);
        }

        void RemoveOne(ClothingItem item)
        {
            if (item == null)
                return;

            if (!wearOrder.TryGetValue(item.layerName, out List<EquippedSlot> list) || list == null)
            {
                UnequipLayer(item.layerName);
                return;
            }

            int index = -1;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null && list[i].itemId == item.itemId)
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
                return;

            bool wasFront = index == list.Count - 1;
            list.RemoveAt(index);
            if (list.Count == 0)
            {
                if (slots.TryGetValue(item.layerName, out LayerSlot main))
                    main.Clear();
                DestroyBack(item.layerName);
                wearOrder.Remove(item.layerName);
                wearByLayer.Remove(item.layerName);
                ClearCompanionOwnedBy(item.layerName);
            }
            else if (wasFront)
            {
                if (slots.TryGetValue(item.layerName, out LayerSlot main)
                    && backSlots.TryGetValue(item.layerName, out LayerSlot back))
                    main.AdoptFrom(back);
                DestroyBack(item.layerName);
                wearByLayer[item.layerName] = list[list.Count - 1];
            }
            else
            {
                DestroyBack(item.layerName);
                wearByLayer[item.layerName] = list[list.Count - 1];
            }

            Equipped?.Invoke(this, null);
            ImportedSave.NoteOutfitChanged();
        }

        void CloneBehind(string layerName, LayerSlot front)
        {
            DestroyBack(layerName);
            if (front == null || front.Root == null)
                return;

            GameObject clone = Instantiate(front.Root.gameObject, front.Root.parent);
            clone.name = layerName + BackSlotSuffix;
            clone.transform.SetSiblingIndex(front.Root.GetSiblingIndex());
            LayerSlot back = LayerSlot.Build(clone.transform);
            if (back == null)
            {
                Destroy(clone);
                return;
            }

            backSlots[layerName] = back;
        }

        void DestroyBack(string layerName)
        {
            if (string.IsNullOrEmpty(layerName) || !backSlots.TryGetValue(layerName, out LayerSlot back))
                return;

            backSlots.Remove(layerName);
            if (back != null && back.Root != null)
                Destroy(back.Root.gameObject);
        }

        void DestroyAllBacks()
        {
            if (backSlots.Count == 0)
                return;

            var names = new List<string>(backSlots.Keys);
            for (int i = 0; i < names.Count; i++)
                DestroyBack(names[i]);
        }

        LayerSlot CreateOverflowLayer(string layerName)
        {
            var rootGo = new GameObject(layerName, typeof(RectTransform));
            rootGo.transform.SetParent(transform, false);
            var root = rootGo.GetComponent<RectTransform>();
            root.anchorMin = new Vector2(0.5f, 0.5f);
            root.anchorMax = new Vector2(0.5f, 0.5f);
            root.pivot = new Vector2(0.5f, 0.5f);
            root.anchoredPosition = Vector2.zero;
            root.sizeDelta = new Vector2(888f, 888f);
            root.localScale = Vector3.one;

            var partGo = new GameObject("S1", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            partGo.transform.SetParent(root, false);
            var part = partGo.GetComponent<RectTransform>();
            part.anchorMin = new Vector2(0.5f, 0.5f);
            part.anchorMax = new Vector2(0.5f, 0.5f);
            part.pivot = new Vector2(0.5f, 0.5f);
            part.anchoredPosition = Vector2.zero;
            part.sizeDelta = new Vector2(888f, 888f);
            Image image = partGo.GetComponent<Image>();
            image.preserveAspect = true;
            image.raycastTarget = false;
            image.color = Color.white;
            image.enabled = false;

            LayerSlot slot = LayerSlot.Build(root);
            if (slot == null)
                return null;
            slots[layerName] = slot;
            root.SetAsLastSibling();
            return slot;
        }

        static bool IsTemplateBangs(ClothingItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.itemId))
                return false;
            if (!string.Equals(item.layerName, "LH", System.StringComparison.OrdinalIgnoreCase))
                return false;
            string id = item.itemId;
            if (id.StartsWith("LH_m", System.StringComparison.OrdinalIgnoreCase)
                || id.StartsWith("LH_p", System.StringComparison.OrdinalIgnoreCase)
                || id.StartsWith("LH_k", System.StringComparison.OrdinalIgnoreCase))
                return false;
            return id.StartsWith("LH_", System.StringComparison.OrdinalIgnoreCase);
        }

        void AppendLhPatch(List<Sprite> fills)
        {
            if (lhS2 == null)
                return;
            for (int i = 0; i < fills.Count; i++)
            {
                if (fills[i] == lhS2)
                    return;
            }

            fills.Add(lhS2);
        }

        void ApplyCompanion(ClothingItem item)
        {
            ClearCompanionOwnedBy(item.layerName);

            if (!item.HasCompanion)
                return;

            string companionLayer = item.tintCompanionLayer;
            if (!slots.TryGetValue(companionLayer, out LayerSlot companionSlot))
            {
                Debug.LogWarning($"[BodyAvatar] 联动层 '{companionLayer}' 不存在", this);
                return;
            }

            companionSlot.ResetTintCache();
            companionSlot.EnsureParts(0, 1);
            companionSlot.ApplyOutlines(null, Color.white);
            Color companionColor = ColorForEquip(item);
            companionSlot.ApplyFills(new List<Sprite> { item.companionSprite }, companionColor);
            companionSlot.SetFillColor(companionColor, LiftForEquip(item), plainEquip ? false : (bool?)null);

            wearByLayer[companionLayer] = new EquippedSlot
            {
                layerName = companionLayer,
                itemId = $"{companionLayer}_sync_{item.itemId}"
            };
            companionOwner[companionLayer] = item.layerName;
        }

        void ClearCompanionOwnedBy(string ownerLayer)
        {
            var toClear = new List<string>();
            foreach (KeyValuePair<string, string> pair in companionOwner)
            {
                if (pair.Value == ownerLayer)
                    toClear.Add(pair.Key);
            }

            for (int i = 0; i < toClear.Count; i++)
            {
                string companionLayer = toClear[i];
                if (slots.TryGetValue(companionLayer, out LayerSlot slot))
                    slot.Clear();
                wearByLayer.Remove(companionLayer);
                companionOwner.Remove(companionLayer);
            }
        }

        public void RememberChosenColor(string itemId, Color color, float lift = 0f)
        {
            if (string.IsNullOrEmpty(itemId))
                return;
            chosenColors[itemId] = color;
            if (lift <= 0.001f)
                chosenLifts.Remove(itemId);
            else
                chosenLifts[itemId] = Mathf.Clamp01(lift);
        }

        public bool TryGetChosenColor(string itemId, out Color color)
        {
            color = default;
            if (string.IsNullOrEmpty(itemId))
                return false;
            return chosenColors.TryGetValue(itemId, out color);
        }

        public float ChosenLift(string itemId)
        {
            if (string.IsNullOrEmpty(itemId))
                return 0f;
            return chosenLifts.TryGetValue(itemId, out float lift) ? lift : 0f;
        }

        public void ClearChosenColors()
        {
            chosenColors.Clear();
            chosenLifts.Clear();
        }

        Color ColorForEquip(ClothingItem item)
        {
            // 批量素穿：忽略旧 chosen（避免先按旧色开曲线），仍用预设色铺图。
            if (plainEquip)
                return item != null ? item.ResolvePresetColor() : Color.white;
            if (item != null && TryGetChosenColor(item.itemId, out Color chosen))
                return chosen;
            return item != null ? item.ResolvePresetColor() : Color.white;
        }

        static void BakeLift(ref Color color, ref float lift)
        {
            float amount = Mathf.Clamp01(lift);
            if (amount > 0.001f)
                color = Color.Lerp(color, Color.white, amount);
            lift = 0f;
        }

        float LiftForEquip(ClothingItem item)
        {
            if (plainEquip)
                return 0f;
            return item != null ? ChosenLift(item.itemId) : 0f;
        }

        /// <summary>只改这一件配饰。同层后面那件在 _back 上，不跟前面那件同一个颜色。</summary>
        public void SetWornPieceColor(string layerName, int pieceIndex, Color color, float lift = 0f, Color? gradientEnd = null)
        {
            if (string.IsNullOrEmpty(layerName))
                return;
            BakeLift(ref color, ref lift);

            if (!wearOrder.TryGetValue(layerName, out List<EquippedSlot> worn) || worn == null || worn.Count == 0)
            {
                SetLayerColor(layerName, color, lift, true, gradientEnd);
                return;
            }

            if (pieceIndex < 0 || pieceIndex >= worn.Count)
                return;

            bool front = pieceIndex == worn.Count - 1;
            LayerSlot target = null;
            if (front)
                slots.TryGetValue(layerName, out target);
            else
                backSlots.TryGetValue(layerName, out target);
            if (target == null)
                slots.TryGetValue(layerName, out target);

            target?.SetFillColor(color, lift, null, gradientEnd);
            if (front)
            {
                foreach (KeyValuePair<string, string> pair in companionOwner)
                {
                    if (pair.Value == layerName && slots.TryGetValue(pair.Key, out LayerSlot companion))
                        companion.SetFillColor(color, lift, null, gradientEnd);
                }
            }

            EquippedSlot piece = worn[pieceIndex];
            if (piece != null)
                RememberChosenColor(piece.itemId, color, lift);
            NoteOutfitIfNeeded();
        }

        public void SetLayerColor(string layerName, Color color, float lift = 0f, bool noteSave = true, Color? gradientEnd = null)
        {
            BakeLift(ref color, ref lift);
            if (!slots.TryGetValue(layerName, out LayerSlot slot))
                return;

            slot.SetFillColor(color, lift, null, gradientEnd);
            if (backSlots.TryGetValue(layerName, out LayerSlot back))
                back.SetFillColor(color, lift, null, gradientEnd);

            // 若该层拥有联动层，同步染色
            foreach (KeyValuePair<string, string> pair in companionOwner)
            {
                if (pair.Value == layerName && slots.TryGetValue(pair.Key, out LayerSlot companion))
                    companion.SetFillColor(color, lift, null, gradientEnd);
            }

            // 若正在改的是被联动层，也反写到主人层（一般不会从 YJT 调色）
            if (companionOwner.TryGetValue(layerName, out string owner)
                && slots.TryGetValue(owner, out LayerSlot ownerSlot))
            {
                ownerSlot.SetFillColor(color, lift, null, gradientEnd);
            }

            if (noteSave)
                NoteOutfitIfNeeded();
        }

        /// <summary>只改这一层的 B 色。不波及没点到的层。</summary>
        public void SetLayerGradientEnd(string layerName, Color end)
        {
            if (slots.TryGetValue(layerName, out LayerSlot slot))
                PaintGradientEnd(slot, end);
            if (backSlots.TryGetValue(layerName, out LayerSlot back))
                PaintGradientEnd(back, end);

            foreach (KeyValuePair<string, string> pair in companionOwner)
            {
                if (pair.Value == layerName && slots.TryGetValue(pair.Key, out LayerSlot companion))
                    PaintGradientEnd(companion, end);
            }

            if (companionOwner.TryGetValue(layerName, out string owner)
                && slots.TryGetValue(owner, out LayerSlot ownerSlot))
                PaintGradientEnd(ownerSlot, end);
        }

        static void PaintGradientEnd(LayerSlot slot, Color end)
        {
            if (slot == null)
                return;
            Image fill = slot.PrimaryFill;
            if (fill == null)
                return;
            slot.SetFillColor(fill.color, SpriteBrighten.Get(fill), null, end);
        }

        public void ClearClothes()
        {
            foreach (KeyValuePair<string, LayerSlot> pair in slots)
            {
                if (IsKept(pair.Key))
                    continue;
                if (swappableLayers.Count > 0 && !CanSwap(pair.Key))
                    continue;
                pair.Value.Clear();
            }

            DestroyAllBacks();
            wearOrder.Clear();
            wearByLayer.Clear();
            companionOwner.Clear();
            NoteOutfitIfNeeded();
        }

        /// <summary>清空衣柜前先卸掉全部图层，包括人模。避免图被删掉后还留在身上，变成黑块。</summary>
        public void StripAllClothes()
        {
            var overflow = new List<string>();
            foreach (KeyValuePair<string, LayerSlot> pair in slots)
            {
                pair.Value?.Clear();
                if (pair.Key == "QJ")
                    overflow.Add(pair.Key);
            }

            for (int i = 0; i < overflow.Count; i++)
            {
                if (slots.TryGetValue(overflow[i], out LayerSlot slot) && slot?.Root != null)
                    Destroy(slot.Root.gameObject);
                slots.Remove(overflow[i]);
            }

            wearOrder.Clear();
            wearByLayer.Clear();
            companionOwner.Clear();
            ClearChosenColors();
            DestroyAllBacks();
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                Transform child = transform.GetChild(i);
                if (child != null && child.name.EndsWith(BackSlotSuffix, StringComparison.OrdinalIgnoreCase))
                    Destroy(child.gameObject);
            }

            Image[] images = GetComponentsInChildren<Image>(true);
            for (int i = 0; i < images.Length; i++)
            {
                Image image = images[i];
                if (image == null || image.sprite != null)
                    continue;
                image.sprite = null;
                image.color = Color.white;
                image.enabled = false;
            }
        }

        public void ClearLayers(IEnumerable<string> layerNames)
        {
            if (layerNames == null)
                return;

            foreach (string layerName in layerNames)
            {
                if (string.IsNullOrEmpty(layerName))
                    continue;
                if (!CanSwap(layerName))
                    continue;

                ClearCompanionOwnedBy(layerName);

                if (slots.TryGetValue(layerName, out LayerSlot slot))
                    slot.Clear();

                DestroyBack(layerName);
                wearOrder.Remove(layerName);
                wearByLayer.Remove(layerName);
            }

            NoteOutfitIfNeeded();
        }

        public OutfitSnapshot CaptureOutfit()
        {
            var snapshot = new OutfitSnapshot { characterId = characterId };
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, List<EquippedSlot>> pair in wearOrder)
            {
                written.Add(pair.Key);
                if (pair.Value == null || companionOwner.ContainsKey(pair.Key))
                    continue;

                for (int i = 0; i < pair.Value.Count; i++)
                {
                    EquippedSlot worn = pair.Value[i];
                    if (worn == null)
                        continue;
                    snapshot.slots.Add(new EquippedSlot
                    {
                        layerName = pair.Key,
                        itemId = worn.itemId
                    });
                }
            }

            foreach (KeyValuePair<string, EquippedSlot> pair in wearByLayer)
            {
                if (pair.Value == null || written.Contains(pair.Key) || companionOwner.ContainsKey(pair.Key))
                    continue;
                snapshot.slots.Add(new EquippedSlot
                {
                    layerName = pair.Key,
                    itemId = pair.Value.itemId
                });
            }

            foreach (KeyValuePair<string, LayerSlot> pair in slots)
            {
                Image fill = pair.Value.PrimaryFill;
                if (fill != null)
                {
                    LayerColorEntry entry = LayerColorEntry.From(pair.Key, fill.color);
                    entry.lift = SpriteBrighten.Get(fill);
                    if (pair.Value.TryGetGradientEnd(out Color end))
                    {
                        entry.hasEnd = 1;
                        entry.er = end.r;
                        entry.eg = end.g;
                        entry.eb = end.b;
                    }
                    snapshot.layerColors.Add(entry);
                }
            }

            if (layerOrderCustomized)
                GetLayerOrder(snapshot.layerOrder);

            return snapshot;
        }

        public void ApplyOutfit(OutfitSnapshot snapshot, ClothingCatalog catalog)
        {
            if (snapshot == null)
                return;

            ClearClothes();

            if (catalog != null && snapshot.slots != null)
            {
                for (int i = 0; i < snapshot.slots.Count; i++)
                {
                    EquippedSlot slot = snapshot.slots[i];
                    if (slot == null)
                        continue;
                    slot.layerName = ImportedSave.LayerNow(slot.layerName);

                    ClothingItem item = ImportedWardrobe.Find(slot.layerName, slot.itemId)
                                       ?? catalog.Find(slot.layerName, slot.itemId)
                                       ?? catalog.Find(slot.itemId);
                    if (item != null)
                        Equip(item);
                }
            }

            // 存档里有顺序就还原；没有（没调过，或旧存档）就回到默认，避免上一份的顺序串过来
            if (snapshot.layerOrder != null && snapshot.layerOrder.Count > 1)
                ApplyLayerOrder(snapshot.layerOrder, notifySave: false);
            else if (layerOrderCustomized)
                ResetLayerOrder(notifySave: false);

            if (snapshot.layerColors == null)
                return;

            for (int i = 0; i < snapshot.layerColors.Count; i++)
            {
                LayerColorEntry entry = snapshot.layerColors[i];
                if (entry != null && !string.IsNullOrEmpty(entry.layerName))
                {
                    entry.layerName = ImportedSave.LayerNow(entry.layerName);
                    Color baked = Color.Lerp(entry.ToColor(), Color.white, Mathf.Clamp01(entry.lift));
                    SetLayerColor(entry.layerName, baked, 0f);
                    var ids = new List<string>();
                    CollectWornItemIds(entry.layerName, ids);
                    for (int n = 0; n < ids.Count; n++)
                        RememberChosenColor(ids[n], baked, 0f);
                }
            }
        }

        public bool TryGetWearOnLayer(string layerName, out EquippedSlot slot)
            => wearByLayer.TryGetValue(layerName, out slot);

        bool CanSwap(string layerName)
        {
            if (swappableLayers == null || swappableLayers.Count == 0)
                return true;
            for (int i = 0; i < swappableLayers.Count; i++)
            {
                if (swappableLayers[i] == layerName)
                    return true;
            }
            return false;
        }

        bool IsKept(string layerName)
        {
            if (keepOnClear == null)
                return false;
            for (int i = 0; i < keepOnClear.Count; i++)
            {
                if (keepOnClear[i] == layerName)
                    return true;
            }
            return false;
        }

        public void RefreshTintStyle()
        {
            RefreshSlotTint(slots);
            RefreshSlotTint(backSlots);
        }

        static void RefreshSlotTint(Dictionary<string, LayerSlot> source)
        {
            foreach (KeyValuePair<string, LayerSlot> pair in source)
            {
                if (string.Equals(pair.Key, "RM", StringComparison.OrdinalIgnoreCase))
                    continue;
                Image fill = pair.Value?.PrimaryFill;
                if (fill == null)
                    continue;
                // 只刷新曲线材质。不把全局止色盖到已经有自己渐变的层上。
                pair.Value.SetFillColor(fill.color, SpriteBrighten.Get(fill), null, null, false);
            }
        }

        sealed class LayerSlot
        {
            public Transform Root;
            public readonly List<Image> Outlines = new List<Image>();
            public readonly List<Image> Fills = new List<Image>();
            public Image Legacy;
            /// <summary>古鸟等：层上只有一张 Image，无 S/B 子件。穿衣只换图，不改 Rect、不生成 888 的 S1。</summary>
            public bool PreferLegacy;

            public Image PrimaryFill => Fills.Count > 0 ? Fills[0] : Legacy;

            public static LayerSlot Build(Transform root)
            {
                var slot = new LayerSlot { Root = root };
                var outlines = new List<(int order, Image img)>();
                var fills = new List<(int order, Image img)>();

                for (int i = 0; i < root.childCount; i++)
                {
                    Transform c = root.GetChild(i);
                    Image img = c.GetComponent<Image>();
                    if (img == null)
                        continue;

                    if (OutlineName.IsMatch(c.name))
                        outlines.Add((ParseIndex(c.name), img));
                    else if (FillName.IsMatch(c.name))
                        fills.Add((ParseIndex(c.name), img));
                }

                outlines.Sort((a, b) => a.order.CompareTo(b.order));
                fills.Sort((a, b) => a.order.CompareTo(b.order));
                for (int i = 0; i < outlines.Count; i++)
                    slot.Outlines.Add(outlines[i].img);
                for (int i = 0; i < fills.Count; i++)
                    slot.Fills.Add(fills[i].img);

                slot.Legacy = root.GetComponent<Image>();
                if (slot.Outlines.Count == 0 && slot.Fills.Count == 0 && slot.Legacy == null)
                    return null;

                slot.PreferLegacy = slot.Outlines.Count == 0 && slot.Fills.Count == 0 && slot.Legacy != null;

                if (slot.Legacy != null && (slot.Outlines.Count > 0 || slot.Fills.Count > 0))
                    slot.Legacy.enabled = false;

                HideIfNoSprite(slot.Legacy);
                for (int i = 0; i < slot.Outlines.Count; i++)
                    HideIfNoSprite(slot.Outlines[i]);
                for (int i = 0; i < slot.Fills.Count; i++)
                    HideIfNoSprite(slot.Fills[i]);

                return slot;
            }

            public void AdoptFrom(LayerSlot other)
            {
                if (other == null)
                    return;

                int fillCount = other.Fills.Count;
                if (fillCount == 0 && other.Legacy != null && other.Legacy.sprite != null)
                    fillCount = 1;
                EnsureParts(other.Outlines.Count, fillCount);
                CopyImages(Fills, other.Fills);
                CopyImages(Outlines, other.Outlines);
                if (Fills.Count == 0 && Legacy != null)
                    CopyImage(Legacy, other.PrimaryFill);
            }

            static void CopyImages(List<Image> dst, List<Image> src)
            {
                for (int i = 0; i < dst.Count; i++)
                {
                    if (src != null && i < src.Count)
                        CopyImage(dst[i], src[i]);
                    else
                        ClearImage(dst[i]);
                }
            }

            static void CopyImage(Image dst, Image src)
            {
                if (dst == null)
                    return;
                if (src == null || src.sprite == null || !src.enabled)
                {
                    ClearImage(dst);
                    return;
                }

                dst.sprite = src.sprite;
                dst.enabled = true;
                dst.color = src.color;
                SpriteBrighten.Set(dst, SpriteBrighten.Get(src));
                RectTransform from = src.rectTransform;
                RectTransform to = dst.rectTransform;
                to.anchorMin = from.anchorMin;
                to.anchorMax = from.anchorMax;
                to.pivot = from.pivot;
                to.anchoredPosition = from.anchoredPosition;
                to.sizeDelta = from.sizeDelta;
                to.localScale = from.localScale;
                to.localRotation = from.localRotation;
            }

            static int ParseIndex(string name)
            {
                if (name.Length <= 1)
                    return 1;
                if (int.TryParse(name.Substring(1), out int n))
                    return n <= 0 ? 1 : n;
                return 1;
            }

            public void EnsureParts(int outlineCount, int fillCount)
            {
                // 古鸟 Legacy：单填色无描边时继续用层上原 Image（800 铺满），勿生成 BodyPanel 的 888 S1
                if (PreferLegacy && outlineCount <= 0 && fillCount <= 1)
                    return;

                if (PreferLegacy)
                    PreferLegacy = false;

                while (Fills.Count < fillCount)
                    Fills.Add(CreatePart($"S{Fills.Count + 1}"));
                while (Outlines.Count < outlineCount)
                    Outlines.Add(CreatePart($"B{Outlines.Count + 1}"));
                // 填色在下、描边在上
                ReorderParts();
            }

            public void ReorderParts()
            {
                int index = 0;
                for (int i = 0; i < Fills.Count; i++)
                {
                    if (Fills[i] != null)
                        Fills[i].transform.SetSiblingIndex(index++);
                }
                for (int i = 0; i < Outlines.Count; i++)
                {
                    if (Outlines[i] != null)
                        Outlines[i].transform.SetSiblingIndex(index++);
                }
            }

            Image CreatePart(string name)
            {
                Transform existing = Root.Find(name);
                GameObject go;
                if (existing != null)
                {
                    go = existing.gameObject;
                    if (go.GetComponent<CanvasRenderer>() == null)
                        go.AddComponent<CanvasRenderer>();
                    if (go.GetComponent<Image>() == null)
                        go.AddComponent<Image>();
                }
                else
                {
                    go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                    go.transform.SetParent(Root, false);
                }

                float canvas = ResolveCanvasSize();
                var rt = go.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = Vector2.zero;
                rt.sizeDelta = new Vector2(canvas, canvas);
                rt.localScale = Vector3.one;
                rt.localRotation = Quaternion.identity;

                Image image = go.GetComponent<Image>();
                image.preserveAspect = true;
                image.raycastTarget = false;
                image.color = Color.white;
                image.enabled = false;
                return image;
            }

            /// <summary>BodyPanel 画布 888；古鸟画布 800。按父面板取边长，避免硬编码放大。</summary>
            float ResolveCanvasSize()
            {
                const float fallback = 888f;
                RectTransform panel = Root != null ? Root.parent as RectTransform : null;
                if (panel != null)
                {
                    float s = Mathf.Max(panel.rect.width, panel.rect.height);
                    if (s > 1f)
                        return s;
                }

                if (Root is RectTransform layerRt)
                {
                    float s = Mathf.Max(layerRt.rect.width, layerRt.rect.height);
                    if (s > 1f)
                        return s;
                }

                return fallback;
            }

            public void ApplyOutlines(List<Sprite> sprites, Color color)
            {
                int count = sprites != null ? sprites.Count : 0;
                for (int i = 0; i < Outlines.Count; i++)
                {
                    if (i < count)
                        ApplyPartSprite(Outlines[i], sprites[i], color);
                    else
                        ClearImage(Outlines[i]);
                }
                ReorderParts();
            }

            public void ApplyFills(List<Sprite> sprites, Color color)
            {
                if (PreferLegacy && Legacy != null)
                {
                    ApplyLegacySprite(Legacy, sprites != null && sprites.Count > 0 ? sprites[0] : null, color);
                    return;
                }

                if (Fills.Count == 0 && Legacy != null)
                {
                    ApplyLegacySprite(Legacy, sprites != null && sprites.Count > 0 ? sprites[0] : null, color);
                    return;
                }

                int count = sprites != null ? sprites.Count : 0;
                for (int i = 0; i < Fills.Count; i++)
                {
                    if (i < count)
                        ApplyPartSprite(Fills[i], sprites[i], color);
                    else
                        ClearImage(Fills[i]);
                }
                ReorderParts();
            }

            public bool TryGetGradientEnd(out Color end)
            {
                end = gradientEndColor;
                return hasGradientEnd;
            }

            Color lastFillColor = new Color(-1f, -1f, -1f, -1f);
            float lastFillLift = -1f;
            int lastFillMode = -1;
            int lastStamp = -1;
            Color lastGradEnd;
            int lastGradKind = -1;
            bool hasGradientEnd;
            Color gradientEndColor;
            int ownedGradKind = -1;
            int ownedMode = -1;

            public void SetFillColor(Color color, float lift = 0f, bool? curveOverride = null, Color? gradientEnd = null, bool shareEnd = true)
            {
                // 人模不参与。没上色时也不开曲线或双色，否则会整层罩灰膜。
                // curveOverride false：批量素穿，曲线和双色都留给随后一次上色。
                // B 色按这一层自己的颜色记，避免没选中的部位跟着变。
                if (gradientEnd.HasValue)
                {
                    hasGradientEnd = true;
                    gradientEndColor = gradientEnd.Value;
                    gradientEndColor.a = 1f;
                }

                int mode = ResolveTintMode(curveOverride, color, shareEnd);
                int grad = ResolveGradKind(curveOverride, color, gradientEnd.HasValue || hasGradientEnd, shareEnd);
                if (shareEnd && curveOverride != false
                    && (mode == TintStyle.Hue || mode == TintStyle.CurveMode))
                    ownedMode = mode;
                if (shareEnd && grad != TintStyle.GradNone && !hasGradientEnd)
                {
                    hasGradientEnd = true;
                    gradientEndColor = TintStyle.GradientEnd;
                }

                if (!shareEnd && !hasGradientEnd)
                    grad = TintStyle.GradNone;
                if (grad == TintStyle.GradLuma || grad == TintStyle.GradVertical)
                    ownedGradKind = grad;

                Color gradEnd = hasGradientEnd ? gradientEndColor : TintStyle.GradientEnd;
                bool sameGrad = grad == TintStyle.GradNone
                    ? lastGradKind == TintStyle.GradNone
                    : lastGradKind == grad && SameRgb(lastGradEnd, gradEnd);
                int stamp = TintStyle.MaterialStamp();
                if (lastFillColor == color && Mathf.Approximately(lastFillLift, lift)
                    && lastFillMode == mode && sameGrad && lastStamp == stamp)
                    return;
                lastFillColor = color;
                lastFillLift = lift;
                lastFillMode = mode;
                lastStamp = stamp;
                lastGradKind = grad;
                lastGradEnd = gradEnd;

                if (Fills.Count == 0 && Legacy != null)
                {
                    Legacy.color = color;
                    SpriteBrighten.Set(Legacy, lift, mode, grad, gradEnd);
                    return;
                }

                for (int i = 0; i < Fills.Count; i++)
                {
                    if (Fills[i] != null && Fills[i].enabled)
                    {
                        Fills[i].color = color;
                        SpriteBrighten.Set(Fills[i], lift, mode, grad, gradEnd);
                    }
                }

                for (int i = 0; i < Outlines.Count; i++)
                {
                    if (Outlines[i] == null || !Outlines[i].enabled)
                        continue;
                    // 明暗渐变只铺色稿。线稿跟起色换色相，笔画明暗不动。
                    if (grad == TintStyle.GradLuma)
                    {
                        Outlines[i].color = color;
                        SpriteBrighten.Set(Outlines[i], 0f, TintStyle.Hue, TintStyle.GradNone, Color.white, true);
                        continue;
                    }
                    if (mode == TintStyle.CurveMode)
                    {
                        Outlines[i].color = SoftOutlineColor(color);
                        SpriteBrighten.Set(Outlines[i], 0f, false);
                    }
                    else
                    {
                        Outlines[i].color = color;
                        SpriteBrighten.Set(Outlines[i], lift, TintStyle.Hue, grad, gradEnd);
                    }
                }
            }

            static bool SameRgb(Color a, Color b)
            {
                return Mathf.Abs(a.r - b.r) < 0.004f
                    && Mathf.Abs(a.g - b.g) < 0.004f
                    && Mathf.Abs(a.b - b.b) < 0.004f;
            }

            int ResolveTintMode(bool? curveOverride, Color color, bool shareEnd)
            {
                if (curveOverride == false || IsBodyModel())
                    return TintStyle.Hue;
                if (!shareEnd)
                {
                    int kept = ownedMode == TintStyle.Hue || ownedMode == TintStyle.CurveMode
                        ? ownedMode
                        : lastFillMode;
                    if (kept == TintStyle.Hue || kept == TintStyle.CurveMode)
                        return kept;
                }
                if (!HasTintColor(color))
                    return TintStyle.Hue;
                return TintStyle.Curve ? TintStyle.CurveMode : TintStyle.Hue;
            }

            int ResolveGradKind(bool? curveOverride, Color color, bool explicitEnd, bool shareEnd)
            {
                if (curveOverride == false || IsBodyModel())
                    return TintStyle.GradNone;
                // 换渐变种类时，已经调好的层继续用自己的那种，不跟着全局改。
                if (!shareEnd && !explicitEnd && hasGradientEnd)
                {
                    int kept = ownedGradKind == TintStyle.GradLuma || ownedGradKind == TintStyle.GradVertical
                        ? ownedGradKind
                        : lastGradKind;
                    if (kept == TintStyle.GradLuma || kept == TintStyle.GradVertical)
                        return kept;
                }
                if (TintStyle.GradientAtOriginal && !explicitEnd && !hasGradientEnd)
                    return TintStyle.GradNone;
                // 明暗渐变只铺已经上过色的色稿。没上色的保持原图，不跟颜色渐变那样靠止色去乘。
                if (TintStyle.GradKind == TintStyle.GradLuma && !HasTintColor(color) && !explicitEnd && !hasGradientEnd)
                    return TintStyle.GradNone;
                if (!HasTintColor(color) && !explicitEnd && !hasGradientEnd)
                    return TintStyle.GradNone;
                return TintStyle.GradKind;
            }

            Color SoftOutlineColor(Color fill)
            {
                if (!IsHairLayer())
                    return Color.white;
                return Color.Lerp(Color.white, fill, HairCurveOutlineMix);
            }

            bool IsHairLayer()
            {
                return Root != null && HairCurveOutlineLayers.Contains(Root.name);
            }

            bool IsBodyModel()
            {
                return Root != null && string.Equals(Root.name, "RM", StringComparison.OrdinalIgnoreCase);
            }

            static bool HasTintColor(Color color)
            {
                return color.r <= 0.96f || color.g <= 0.96f || color.b <= 0.96f;
            }

            public void Clear()
            {
                ResetTintCache();
                for (int i = 0; i < Outlines.Count; i++)
                    ClearImage(Outlines[i]);
                for (int i = 0; i < Fills.Count; i++)
                    ClearImage(Fills[i]);
                if (Legacy != null && Fills.Count == 0)
                    ClearImage(Legacy);
            }

            /// <summary>古鸟 Legacy：只换图/颜色，保留场景里 stretch 铺满 BodyPanel 的布局。</summary>
            static void ApplyLegacySprite(Image image, Sprite sprite, Color color)
            {
                if (image == null)
                    return;
                image.sprite = sprite;
                image.enabled = sprite != null;
                if (sprite != null)
                    image.color = color;
            }

            void ApplyPartSprite(Image image, Sprite sprite, Color color)
            {
                if (image == null)
                    return;
                image.sprite = sprite;
                image.enabled = sprite != null;
                if (sprite == null)
                    return;

                image.color = color;
                var rt = image.rectTransform;
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                if (ImportedSpriteFrames.TryGet(sprite, out Vector2 anchored, out Vector2 size))
                {
                    rt.anchoredPosition = anchored;
                    rt.sizeDelta = size;
                    return;
                }

                // S/B：按所在 BodyPanel 画布居中（888 或其它），勿 SetNativeSize
                float canvas = ResolveCanvasSize();
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = Vector2.zero;
                rt.sizeDelta = new Vector2(canvas, canvas);
            }

            static void HideIfNoSprite(Image image)
            {
                if (image != null && image.sprite == null)
                    image.enabled = false;
            }

            static void ClearImage(Image image)
            {
                if (image == null)
                    return;
                image.sprite = null;
                image.color = Color.white;
                image.enabled = false;
                SpriteBrighten.Set(image, 0f, TintStyle.Hue, TintStyle.GradNone, Color.white);
            }

            public void ResetTintCache()
            {
                lastFillColor = new Color(-1f, -1f, -1f, -1f);
                lastFillLift = -1f;
                lastFillMode = -1;
                lastStamp = -1;
                lastGradKind = -1;
                lastGradEnd = default;
                hasGradientEnd = false;
                gradientEndColor = Color.white;
                ownedGradKind = -1;
                ownedMode = -1;
            }
        }

#if UNITY_EDITOR
        void OnValidate()
        {
            if (string.IsNullOrWhiteSpace(characterId))
                characterId = gameObject.name;
        }
#endif
    }
}
