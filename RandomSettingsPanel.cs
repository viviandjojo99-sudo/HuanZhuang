using System.Collections.Generic;
using System.Text;
using DressUp.Avatar;
using DressUp.Data;
using DressUp.Import;
using DressUp.Tinting;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DressUp.Actions
{
    /// <summary>
    /// 随机颜色、随机部件、同时变色、色系保持一致。渐变开着时可以锁定渐变色。勾选会记住。
    /// 第一次打开：随机颜色只勾头发，随机部件全勾，同时变色都不勾。
    /// </summary>
    [ExecuteAlways]
    public class RandomSettingsPanel : MonoBehaviour
    {
        static readonly string[] Options = { "头发", "衣服", "裤子", "鞋子", "袜子", "配饰", "其它", "外套" };
        const int OtherGroup = 6;
        const int CoatGroup = 7;
        const int LinkCount = 8;
        /// <summary>衣裤：外套+衣服+裤子。</summary>
        static readonly int[] OutfitGroups = { 1, 2, CoatGroup };
        /// <summary>鞋袜：鞋子+袜子。</summary>
        static readonly int[] FootwearGroups = { 3, 4 };

        static readonly string[][] FixedLayers =
        {
            new[] { "DM", "BF", "LH", "CF", "HF", "HFBZ", "SF", "BZ" },
            new[] { "YF" },
            new[] { "KZ" },
            new[] { "XZ" },
            new[] { "WZ" },
            new[] { "BS", "TSQ", "TSH", "DS", "HFBZZS", "YS", "ED", "WB", "SE", "J", "CJ", "GG" },
            new[] { "YJ", "Z" },
            new[] { "WT" }
        };

        const string ColorKey = "DressUp.RandomColorParts";
        const string PartKey = "DressUp.RandomPartChecks";
        const string LinkKey = "DressUp.TintLinkChecks";
        const string CoatKey = "DressUp.CoatWithClothes";
        const string PaletteKey = "DressUp.SamePalette";
        const string LockKey = "DressUp.LockGradient";

        [Header("圈圈查找字样。文案改了，就把对应这一项改成那一行的新开头。")]
        [InspectorName("曲线变色")]
        [SerializeField] string curveLookup = "曲线变色";
        [InspectorName("色相变色")]
        [SerializeField] string hueLookup = "色相变色";
        [InspectorName("色系保持一致")]
        [SerializeField] string paletteLookup = "色系保持一致";
        [InspectorName("颜色渐变")]
        [SerializeField] string verticalLookup = "颜色渐变";
        [InspectorName("明暗渐变")]
        [SerializeField] string lumaLookup = "明暗渐变";
        [InspectorName("锁定渐变色")]
        [SerializeField] string lockLookup = "锁定渐变色";

        readonly bool[] colorChecks = new bool[Options.Length];
        readonly bool[] partChecks = new bool[Options.Length];
        readonly bool[] linkChecks = new bool[LinkCount];
        [System.NonSerialized] bool built;
        [System.NonSerialized] string editorStamp;
        [System.NonSerialized] string lockLineBody;
        bool samePalette;
        bool lockGradient;
        [System.NonSerialized] Image curveDot;
        [System.NonSerialized] Image hueDot;
        [System.NonSerialized] Image verticalDot;
        [System.NonSerialized] Image lumaDot;
        [System.NonSerialized] Image paletteDot;
        [System.NonSerialized] Image lockDot;
        [System.NonSerialized] GameObject lockRow;
        [System.NonSerialized] TMP_Text tintColumn;
        [System.NonSerialized] Sprite ringSprite;
        [System.NonSerialized] Sprite dotSprite;

        public static RandomSettingsPanel Current { get; private set; }

        public bool SamePalette => samePalette;

        /// <summary>渐变开着且勾了锁定：这轮上色用调色盘上的一对颜色。</summary>
        public bool LockGradientActive => lockGradient && TintStyle.TwoColor;

        /// <summary>同时变色勾选位图，拖色条联动缓存用，改勾选后立刻变化。</summary>
        public int TintLinkMask
        {
            get
            {
                int mask = 0;
                for (int i = 0; i < linkChecks.Length; i++)
                {
                    if (linkChecks[i])
                        mask |= 1 << i;
                }

                return mask;
            }
        }

        public static void Wire()
        {
            Transform panel = FindNamed("RandomSettings");
            if (panel == null)
                return;

            RandomSettingsPanel settings = panel.GetComponent<RandomSettingsPanel>();
            if (settings == null)
                settings = panel.gameObject.AddComponent<RandomSettingsPanel>();
            settings.Bind(FindNamed("ButtonSettings"));
            PadHost.Wire();
        }

        void OnEnable()
        {
            if (!Application.isPlaying)
                return;
            DestroyGeneratedCircles();
            built = false;
            Current = this;
        }

#if UNITY_EDITOR
        void Update()
        {
            if (Application.isPlaying)
                return;

            string stamp = EditorStamp();
            if (built && stamp == editorStamp)
                return;

            editorStamp = stamp;
            ClearEditorCircles();
            built = false;
            lockRow = null;
            EnsureCircles();
        }

        string EditorStamp()
        {
            var stamp = new StringBuilder();
            string[] names = { "randomColor", "randomPart", "ColorChangeSettings", "ColorChangeSettings 2" };
            for (int i = 0; i < names.Length; i++)
            {
                Transform column = transform.Find(names[i]);
                TMP_Text label = column != null ? column.GetComponent<TMP_Text>() : null;
                if (label == null)
                    continue;
                RectTransform rect = label.rectTransform;
                stamp.Append(label.text).Append('|');
                stamp.Append(rect.anchoredPosition.x).Append(',').Append(rect.anchoredPosition.y).Append('|');
                stamp.Append(rect.sizeDelta.x).Append(',').Append(rect.sizeDelta.y).Append('|');
                stamp.Append(label.fontSize).Append(';');
            }

            stamp.Append(curveLookup).Append(';');
            stamp.Append(hueLookup).Append(';');
            stamp.Append(paletteLookup).Append(';');
            stamp.Append(verticalLookup).Append(';');
            stamp.Append(lumaLookup).Append(';');
            stamp.Append(lockLookup);
            return stamp.ToString();
        }

        void ClearEditorCircles()
        {
            DestroyGeneratedCircles();
        }
#endif

        void DestroyGeneratedCircles()
        {
            Transform[] all = GetComponentsInChildren<Transform>(true);
            for (int i = all.Length - 1; i >= 0; i--)
            {
                if (all[i] == null || !all[i].name.StartsWith("rs-circle:", System.StringComparison.Ordinal))
                    continue;
                Transform parent = all[i].parent;
                if (parent != null && parent.name.StartsWith("rs-circle:", System.StringComparison.Ordinal))
                    continue;
                DestroyImmediate(all[i].gameObject);
            }

            curveDot = null;
            hueDot = null;
            verticalDot = null;
            lumaDot = null;
            paletteDot = null;
            lockDot = null;
            lockRow = null;
        }

        static void MarkCircle(GameObject go)
        {
            go.hideFlags = HideFlags.DontSave;
            if (!go.name.StartsWith("rs-circle:", System.StringComparison.Ordinal))
                go.name = "rs-circle:" + go.name;
        }

        void OnDestroy()
        {
            if (Current == this)
                Current = null;
        }

        void Bind(Transform opener)
        {
            LoadChecks();
            Current = this;
            if (opener != null)
            {
                Button open = opener.GetComponent<Button>();
                if (open != null)
                {
                    open.onClick.RemoveListener(Open);
                    open.onClick.AddListener(Open);
                }
            }

        }

        public void Open()
        {
            PadHost.Show("RandomSettings");
            IgnoreTitleClicks();
            EnsureCircles();
        }

        public void Close()
        {
            PadHost.Close();
        }

        public bool PartEnabled(string layerName, ClothingCatalog catalog)
        {
            int group = GroupOf(layerName, catalog);
            if (group < 0 || group >= partChecks.Length)
                return true;
            return partChecks[group];
        }

        public bool ColorEnabled(string layerName, ClothingCatalog catalog)
        {
            int group = GroupOf(layerName, catalog);
            if (group < 0 || group >= colorChecks.Length)
                return false;
            return colorChecks[group];
        }

        public bool CollectLinked(string layerName, ClothingCatalog catalog, List<string> into)
        {
            if (into == null)
                return false;
            into.Clear();
            int group = GroupOf(layerName, catalog);
            if (group < 0)
                return false;

            int[] bundle = BundleOf(group);
            if (bundle != null)
            {
                if (!AllBundleLinked(bundle))
                    return false;
                CollectBundleLayers(bundle, catalog, into);
                return into.Count > 0;
            }

            if (group >= linkChecks.Length || !linkChecks[group])
                return false;

            LayersFor(group, catalog, into);
            return into.Count > 0;
        }

        /// <summary>色系保持一致：收集全身可调色层。</summary>
        public bool CollectAllTintLayers(ClothingCatalog catalog, List<string> into)
        {
            if (into == null)
                return false;
            into.Clear();
            var part = new List<string>();
            var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < FixedLayers.Length; i++)
            {
                LayersFor(i, catalog, part);
                for (int n = 0; n < part.Count; n++)
                {
                    if (seen.Add(part[n]))
                        into.Add(part[n]);
                }
            }

            return into.Count > 0;
        }

        static int[] BundleOf(int group)
        {
            if (ContainsGroup(OutfitGroups, group))
                return OutfitGroups;
            if (ContainsGroup(FootwearGroups, group))
                return FootwearGroups;
            return null;
        }

        static bool ContainsGroup(int[] groups, int group)
        {
            for (int i = 0; i < groups.Length; i++)
            {
                if (groups[i] == group)
                    return true;
            }

            return false;
        }

        bool AllBundleLinked(int[] groups)
        {
            for (int i = 0; i < groups.Length; i++)
            {
                int index = groups[i];
                if (index >= linkChecks.Length || !linkChecks[index])
                    return false;
            }

            return true;
        }

        void CollectBundleLayers(int[] groups, ClothingCatalog catalog, List<string> into)
        {
            var part = new List<string>();
            var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < groups.Length; i++)
            {
                LayersFor(groups[i], catalog, part);
                for (int n = 0; n < part.Count; n++)
                {
                    if (seen.Add(part[n]))
                        into.Add(part[n]);
                }
            }
        }

        public bool HasColorChecks()
        {
            for (int i = 0; i < colorChecks.Length; i++)
            {
                if (colorChecks[i])
                    return true;
            }

            return false;
        }

        public bool ApplyChecked(BodyAvatar avatar, ClothingCatalog catalog, LayerColorController colorController, out Color lastColor)
        {
            lastColor = Color.white;
            if (avatar == null)
                return false;

            if (LockGradientActive)
                return PaintLocked(avatar, catalog, out lastColor);

            float saturation = 0.5f;
            if (colorController != null)
            {
                Color.RGBToHSV(colorController.BarColor, out _, out saturation, out _);
                if (saturation < 0.08f)
                    saturation = 0.5f;
            }

            // 色系保持一致：本轮随机只抽一次色相，全身勾选组合并一次上色。
            if (samePalette)
            {
                var layers = new List<string>();
                var part = new List<string>();
                var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                var ids = new List<string>();
                for (int i = 0; i < colorChecks.Length; i++)
                {
                    if (!colorChecks[i])
                        continue;
                    LayersFor(i, catalog, part);
                    for (int n = 0; n < part.Count; n++)
                    {
                        if (seen.Add(part[n]))
                            layers.Add(part[n]);
                    }
                }

                return PaintSameColor(avatar, layers, saturation, Random.value, ids, out lastColor);
            }

            float? sharedHue = null;
            bool any = false;
            bool outfitBatchDone = false;
            bool footwearBatchDone = false;
            var done = new HashSet<int>();
            var paintIds = new List<string>();
            var paintLayers = new List<string>();
            var paintPart = new List<string>();

            for (int i = 0; i < colorChecks.Length; i++)
            {
                if (!colorChecks[i] || done.Contains(i))
                    continue;

                int[] bundle = BundleOf(i);
                bool linked = bundle != null
                    ? AllBundleLinked(bundle)
                    : i < linkChecks.Length && linkChecks[i];

                // 同时变色勾了衣裤/鞋袜：随机也整组同一色。
                if (bundle != null && linked)
                {
                    bool isOutfit = bundle == OutfitGroups;
                    if (isOutfit)
                    {
                        if (outfitBatchDone)
                            continue;
                        outfitBatchDone = true;
                    }
                    else
                    {
                        if (footwearBatchDone)
                            continue;
                        footwearBatchDone = true;
                    }

                    for (int c = 0; c < bundle.Length; c++)
                        done.Add(bundle[c]);

                    paintLayers.Clear();
                    var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                    for (int c = 0; c < bundle.Length; c++)
                    {
                        if (!colorChecks[bundle[c]])
                            continue;
                        LayersFor(bundle[c], catalog, paintPart);
                        for (int n = 0; n < paintPart.Count; n++)
                        {
                            if (seen.Add(paintPart[n]))
                                paintLayers.Add(paintPart[n]);
                        }
                    }

                    if (PaintSameColor(avatar, paintLayers, saturation, sharedHue, paintIds, out lastColor))
                        any = true;
                    continue;
                }

                done.Add(i);
                LayersFor(i, catalog, paintLayers);
                if (paintLayers.Count == 0)
                    continue;

                // 配饰未勾同时变色：每件各随机一色。勾了则同组一色。
                if (i == 5 && !linked)
                {
                    if (PaintEachPiece(avatar, paintLayers, saturation, sharedHue, paintIds, out lastColor))
                        any = true;
                    continue;
                }

                if (PaintSameColor(avatar, paintLayers, saturation, sharedHue, paintIds, out lastColor))
                    any = true;
            }

            return any;
        }

        bool PaintLocked(BodyAvatar avatar, ClothingCatalog catalog, out Color lastColor)
        {
            var layers = new List<string>();
            var part = new List<string>();
            var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < colorChecks.Length; i++)
            {
                if (!colorChecks[i])
                    continue;
                LayersFor(i, catalog, part);
                for (int n = 0; n < part.Count; n++)
                {
                    if (seen.Add(part[n]))
                        layers.Add(part[n]);
                }
            }

            var ids = new List<string>();
            return PaintSameColor(avatar, layers, 1f, null, true, ids, out lastColor);
        }

        static bool PaintSameColor(BodyAvatar avatar, List<string> layers, float saturation, float? fixedHue, List<string> ids, out Color lastColor)
        {
            return PaintSameColor(avatar, layers, saturation, fixedHue, false, ids, out lastColor);
        }

        static bool PaintSameColor(BodyAvatar avatar, List<string> layers, float saturation, float? fixedHue, bool locked, List<string> ids, out Color lastColor)
        {
            lastColor = Color.white;
            Color color;
            Color? gradientEnd = null;
            if (locked)
            {
                color = TintStyle.GradientStart;
                color.a = 1f;
                Color end = TintStyle.GradientEnd;
                end.a = 1f;
                gradientEnd = end;
            }
            else
            {
                float hue = fixedHue ?? Random.value;
                color = Color.HSVToRGB(hue, saturation, 1f);
                color.a = 1f;
                if (TintStyle.TwoColor)
                {
                    Color end = Color.HSVToRGB(Mathf.Repeat(hue + 0.18f, 1f), saturation, 1f);
                    TintStyle.SetGradientPair(color, end);
                    gradientEnd = end;
                }
            }

            ids.Clear();
            bool painted = false;
            for (int L = 0; L < layers.Count; L++)
            {
                string layer = layers[L];
                if (!avatar.TryGetWearOnLayer(layer, out EquippedSlot worn) || worn == null)
                    continue;
                avatar.SetLayerColor(layer, color, 0f, false, gradientEnd);
                avatar.CollectWornItemIds(layer, ids);
                painted = true;
            }

            if (!painted)
                return false;

            for (int n = 0; n < ids.Count; n++)
                avatar.RememberChosenColor(ids[n], color);
            lastColor = color;
            if (!avatar.IsOutfitBatching)
                ImportedSave.NoteOutfitChanged();
            return true;
        }

        static bool PaintEachPiece(BodyAvatar avatar, List<string> layers, float saturation, float? fixedHue, List<string> ids, out Color lastColor)
        {
            lastColor = Color.white;
            ids.Clear();
            bool painted = false;
            for (int L = 0; L < layers.Count; L++)
            {
                string layer = layers[L];
                if (!avatar.TryGetWearOnLayer(layer, out EquippedSlot worn) || worn == null)
                    continue;

                int before = ids.Count;
                avatar.CollectWornItemIds(layer, ids);
                for (int piece = before; piece < ids.Count; piece++)
                {
                    float hue = fixedHue ?? Random.value;
                    Color color = Color.HSVToRGB(hue, saturation, 1f);
                    color.a = 1f;
                    Color? gradientEnd = null;
                    if (TintStyle.TwoColor)
                    {
                        Color end = Color.HSVToRGB(Mathf.Repeat(hue + 0.18f, 1f), saturation, 1f);
                        TintStyle.SetGradientPair(color, end);
                        gradientEnd = end;
                    }

                    avatar.SetWornPieceColor(layer, piece - before, color, 0f, gradientEnd);
                    lastColor = color;
                    painted = true;
                }
            }

            return painted;
        }

        int GroupOf(string layerName, ClothingCatalog catalog)
        {
            if (string.IsNullOrEmpty(layerName))
                return -1;

            if (catalog?.tags != null)
            {
                for (int i = 0; i < catalog.tags.Count; i++)
                {
                    WardrobeTagDef tag = catalog.tags[i];
                    if (tag?.layers == null || !ContainsLayer(tag.layers, layerName))
                        continue;
                    int named = GroupByTag(tag.tagName, tag.parentTag);
                    if (named >= 0)
                        return named;
                }
            }

            for (int i = 0; i < FixedLayers.Length; i++)
            {
                if (i == OtherGroup)
                    continue;
                if (ContainsName(FixedLayers[i], layerName))
                    return i;
            }

            return OtherGroup;
        }

        static int GroupByTag(string tagName, string parent)
        {
            // 发饰/发带是配饰，不跟头发一起变色。
            if (IsName(tagName, "发饰") || IsName(tagName, "发带"))
                return 5;
            if (IsName(tagName, "头发") || IsName(parent, "头发"))
                return 0;
            if (IsName(tagName, "衣服") || IsName(parent, "衣服"))
                return 1;
            if (IsName(tagName, "裤子") || IsName(parent, "裤子"))
                return 2;
            if (IsName(tagName, "鞋子") || IsName(parent, "鞋子"))
                return 3;
            if (IsName(tagName, "袜子") || IsName(parent, "袜子"))
                return 4;
            if (IsName(tagName, "外套") || IsName(parent, "外套"))
                return CoatGroup;
            if (IsName(tagName, "其它饰品") || IsName(parent, "其它饰品") || IsName(tagName, "配饰"))
                return 5;
            return -1;
        }

        void LayersFor(int group, ClothingCatalog catalog, List<string> into)
        {
            into.Clear();
            if (group < 0 || group >= FixedLayers.Length)
                return;

            var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            if (group == 0 && catalog != null)
            {
                List<string> hair = AvatarRandomButton.CollectHairTintLayers(catalog);
                for (int i = 0; i < hair.Count; i++)
                {
                    if (seen.Add(hair[i]))
                        into.Add(hair[i]);
                }
            }

            if (catalog?.tags != null && group != OtherGroup)
            {
                for (int i = 0; i < catalog.tags.Count; i++)
                {
                    WardrobeTagDef tag = catalog.tags[i];
                    if (tag?.layers == null || GroupByTag(tag.tagName, tag.parentTag) != group)
                        continue;
                    for (int L = 0; L < tag.layers.Count; L++)
                    {
                        string layer = tag.layers[L];
                        if (!string.IsNullOrEmpty(layer) && seen.Add(layer))
                            into.Add(layer);
                    }
                }
            }

            if (group == OtherGroup && catalog?.tags != null)
            {
                for (int i = 0; i < catalog.tags.Count; i++)
                {
                    WardrobeTagDef tag = catalog.tags[i];
                    if (tag?.layers == null)
                        continue;
                    if (GroupByTag(tag.tagName, tag.parentTag) >= 0)
                        continue;
                    if (IsName(tag.tagName, "人模") || IsName(tag.parentTag, "人模"))
                        continue;
                    for (int L = 0; L < tag.layers.Count; L++)
                    {
                        string layer = tag.layers[L];
                        if (string.IsNullOrEmpty(layer) || IsName(layer, "RM") || IsName(layer, "WT"))
                            continue;
                        if (seen.Add(layer))
                            into.Add(layer);
                    }
                }
            }

            string[] fallback = FixedLayers[group];
            for (int i = 0; i < fallback.Length; i++)
            {
                if (seen.Add(fallback[i]))
                    into.Add(fallback[i]);
            }
        }

        static bool ContainsLayer(List<string> layers, string layerName)
        {
            for (int i = 0; i < layers.Count; i++)
            {
                if (string.Equals(layers[i], layerName, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        static bool ContainsName(string[] names, string layerName)
        {
            for (int i = 0; i < names.Length; i++)
            {
                if (string.Equals(names[i], layerName, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        static bool IsName(string value, string expected)
        {
            return string.Equals(value, expected, System.StringComparison.Ordinal);
        }

        void EnsureCircles()
        {
            if (built)
                return;

            if (ringSprite == null)
                ringSprite = MakeDisc(true);
            if (dotSprite == null)
                dotSprite = MakeDisc(false);

            int placed = 0;
            placed += BuildColumn("randomColor", colorChecks, SaveColor);
            placed += BuildColumn("randomPart", partChecks, SavePart);
            placed += BuildColumn("ColorChangeSettings", linkChecks, SaveLink);
            placed += BuildTintMode();
            SyncTintDots();
            built = placed > 0;
        }

        int BuildTintMode()
        {
            Transform column = transform.Find("ColorChangeSettings 2");
            TMP_Text columnLabel = column != null ? column.GetComponent<TMP_Text>() : null;
            if (columnLabel == null)
                return 0;
            tintColumn = columnLabel;

            columnLabel.raycastTarget = false;
            columnLabel.ForceMeshUpdate();
            TMP_TextInfo info = columnLabel.textInfo;
            if (info == null || info.lineCount == 0)
                return 0;

            int placed = 0;
            var text = new StringBuilder();
            for (int line = 0; line < info.lineCount; line++)
            {
                int first = info.lineInfo[line].firstCharacterIndex;
                int last = info.lineInfo[line].lastCharacterIndex;
                if (first < 0 || last < first || last >= info.characterCount)
                    continue;

                text.Length = 0;
                for (int c = first; c <= last; c++)
                {
                    char ch = info.characterInfo[c].character;
                    if (ch == '\0' || ch == '\n' || ch == '\r')
                        continue;
                    text.Append(ch);
                }

                string label = text.ToString().Trim();
                int role = BestLookup(label);
                if (role == RoleLock)
                {
                    lockDot = CreateLockCircle(columnLabel, label, info.characterInfo[first], out lockRow);
                    placed++;
                    continue;
                }

                if (role == RolePalette)
                {
                    paletteDot = CreatePaletteCircle(columnLabel, label, info.characterInfo[first]);
                    placed++;
                    continue;
                }

                if (role == RoleVertical || role == RoleLuma)
                {
                    int kind = role == RoleVertical ? TintStyle.GradVertical : TintStyle.GradLuma;
                    Image gradDot = CreateGradCircle(columnLabel, label, kind, info.characterInfo[first]);
                    if (role == RoleVertical)
                        verticalDot = gradDot;
                    else
                        lumaDot = gradDot;
                    placed++;
                    continue;
                }

                if (role != RoleCurve && role != RoleHue)
                    continue;

                int mode = role == RoleCurve ? TintStyle.CurveMode : TintStyle.Hue;
                Image dot = CreateModeCircle(columnLabel, label, mode, info.characterInfo[first]);
                if (role == RoleCurve)
                    curveDot = dot;
                else
                    hueDot = dot;
                placed++;
            }

            return placed;
        }

        Image CreateModeCircle(TMP_Text columnLabel, string name, int modeChoice, TMP_CharacterInfo character)
        {
            var go = new GameObject(
                name,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image),
                typeof(Button));
            go.transform.SetParent(columnLabel.rectTransform, false);
            MarkCircle(go);

            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(40f, 40f);
            float midY = (character.ascender + character.descender) * 0.5f;
            rect.anchoredPosition = new Vector2(character.origin - 48f, midY);

            Image image = go.GetComponent<Image>();
            image.sprite = ringSprite;
            image.color = columnLabel.color;
            image.raycastTarget = true;

            var dotGo = new GameObject("Dot", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            dotGo.transform.SetParent(go.transform, false);
            MarkCircle(dotGo);
            var dotRect = dotGo.GetComponent<RectTransform>();
            dotRect.anchorMin = dotRect.anchorMax = new Vector2(0.5f, 0.5f);
            dotRect.pivot = new Vector2(0.5f, 0.5f);
            dotRect.sizeDelta = new Vector2(20f, 20f);
            dotRect.anchoredPosition = Vector2.zero;
            Image dot = dotGo.GetComponent<Image>();
            dot.sprite = dotSprite;
            dot.color = columnLabel.color;
            dot.raycastTarget = false;
            dot.enabled = TintStyle.Mode == modeChoice;

            Button button = go.GetComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = image;
            int choose = modeChoice;
            button.onClick.AddListener(() => ChooseTintMode(choose));
            return dot;
        }

        Image CreateGradCircle(TMP_Text columnLabel, string name, int kind, TMP_CharacterInfo character)
        {
            var go = new GameObject(
                name,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image),
                typeof(Button));
            go.transform.SetParent(columnLabel.rectTransform, false);
            MarkCircle(go);

            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(40f, 40f);
            float midY = (character.ascender + character.descender) * 0.5f;
            rect.anchoredPosition = new Vector2(character.origin - 48f, midY);

            Image image = go.GetComponent<Image>();
            image.sprite = ringSprite;
            image.color = columnLabel.color;
            image.raycastTarget = true;

            var dotGo = new GameObject("Dot", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            dotGo.transform.SetParent(go.transform, false);
            MarkCircle(dotGo);
            var dotRect = dotGo.GetComponent<RectTransform>();
            dotRect.anchorMin = dotRect.anchorMax = new Vector2(0.5f, 0.5f);
            dotRect.pivot = new Vector2(0.5f, 0.5f);
            dotRect.sizeDelta = new Vector2(20f, 20f);
            dotRect.anchoredPosition = Vector2.zero;
            Image dot = dotGo.GetComponent<Image>();
            dot.sprite = dotSprite;
            dot.color = columnLabel.color;
            dot.raycastTarget = false;
            dot.enabled = TintStyle.GradKind == kind;

            Button button = go.GetComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = image;
            int choose = kind;
            button.onClick.AddListener(() => ToggleGradKind(choose));
            return dot;
        }

        Image CreatePaletteCircle(TMP_Text columnLabel, string name, TMP_CharacterInfo character)
        {
            var go = new GameObject(
                name,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image),
                typeof(Button));
            go.transform.SetParent(columnLabel.rectTransform, false);
            MarkCircle(go);

            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(40f, 40f);
            float midY = (character.ascender + character.descender) * 0.5f;
            rect.anchoredPosition = new Vector2(character.origin - 48f, midY);

            Image image = go.GetComponent<Image>();
            image.sprite = ringSprite;
            image.color = columnLabel.color;
            image.raycastTarget = true;

            var dotGo = new GameObject("Dot", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            dotGo.transform.SetParent(go.transform, false);
            MarkCircle(dotGo);
            var dotRect = dotGo.GetComponent<RectTransform>();
            dotRect.anchorMin = dotRect.anchorMax = new Vector2(0.5f, 0.5f);
            dotRect.pivot = new Vector2(0.5f, 0.5f);
            dotRect.sizeDelta = new Vector2(20f, 20f);
            dotRect.anchoredPosition = Vector2.zero;
            Image dot = dotGo.GetComponent<Image>();
            dot.sprite = dotSprite;
            dot.color = columnLabel.color;
            dot.raycastTarget = false;
            dot.enabled = samePalette;

            Button button = go.GetComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = image;
            button.onClick.AddListener(ToggleSamePalette);
            return dot;
        }

        Image CreateLockCircle(TMP_Text columnLabel, string name, TMP_CharacterInfo character, out GameObject row)
        {
            var go = new GameObject(
                name,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image),
                typeof(Button));
            go.transform.SetParent(columnLabel.rectTransform, false);
            MarkCircle(go);
            row = go;

            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(40f, 40f);
            PlaceCircle(rect, character);

            Image image = go.GetComponent<Image>();
            image.sprite = ringSprite;
            image.color = columnLabel.color;
            image.raycastTarget = true;

            var dotGo = new GameObject("Dot", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            dotGo.transform.SetParent(go.transform, false);
            MarkCircle(dotGo);
            var dotRect = dotGo.GetComponent<RectTransform>();
            dotRect.anchorMin = dotRect.anchorMax = new Vector2(0.5f, 0.5f);
            dotRect.pivot = new Vector2(0.5f, 0.5f);
            dotRect.sizeDelta = new Vector2(20f, 20f);
            dotRect.anchoredPosition = Vector2.zero;
            Image dot = dotGo.GetComponent<Image>();
            dot.sprite = dotSprite;
            dot.color = columnLabel.color;
            dot.raycastTarget = false;
            dot.enabled = lockGradient;

            Button button = go.GetComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = image;
            button.onClick.AddListener(ToggleLockGradient);
            return dot;
        }

        void ToggleLockGradient()
        {
            if (!TintStyle.TwoColor)
                return;
            lockGradient = !lockGradient;
            if (lockDot != null)
                lockDot.enabled = lockGradient;
            PlayerPrefs.SetInt(LockKey, lockGradient ? 1 : 0);
            PlayerPrefs.Save();
        }

        const int RoleNone = 0;
        const int RoleCurve = 1;
        const int RoleHue = 2;
        const int RolePalette = 3;
        const int RoleVertical = 4;
        const int RoleLuma = 5;
        const int RoleLock = 6;

        int BestLookup(string label)
        {
            int best = RoleNone;
            int bestLen = -1;
            best = Longer(label, curveLookup, RoleCurve, best, ref bestLen);
            best = Longer(label, hueLookup, RoleHue, best, ref bestLen);
            best = Longer(label, paletteLookup, RolePalette, best, ref bestLen);
            best = Longer(label, verticalLookup, RoleVertical, best, ref bestLen);
            best = Longer(label, lumaLookup, RoleLuma, best, ref bestLen);
            best = Longer(label, lockLookup, RoleLock, best, ref bestLen);
            return best;
        }

        static int Longer(string label, string lookup, int role, int best, ref int bestLen)
        {
            if (string.IsNullOrWhiteSpace(lookup) || !LineMatches(label, lookup))
                return best;
            int len = lookup.Trim().Length;
            if (len <= bestLen)
                return best;
            bestLen = len;
            return role;
        }

        /// <summary>没开渐变时拿掉锁定这一行。开了渐变再放回来。</summary>
        void RefreshLockRow()
        {
            if (tintColumn == null)
                return;

            string text = tintColumn.text.Replace("\r", "");
            string found = MatchingLine(text, lockLookup);
            if (!string.IsNullOrEmpty(found))
                lockLineBody = found;

            TMP_CharacterInfo character;
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                tintColumn.ForceMeshUpdate();
                if (lockRow != null)
                {
                    lockRow.SetActive(true);
                    if (TryFindLine(tintColumn, lockLookup, out character))
                        PlaceCircle(lockRow.GetComponent<RectTransform>(), character);
                }
                return;
            }
#endif

            bool show = TintStyle.TwoColor;
            string body = string.IsNullOrEmpty(lockLineBody) ? lockLookup : lockLineBody;
            bool has = MatchingLine(text, lockLookup) != null;
            if (show && !has && !string.IsNullOrEmpty(body))
            {
                if (text.Length > 0 && !text.EndsWith("\n"))
                    text += "\n";
                tintColumn.text = text + body;
            }
            else if (!show && has)
            {
                tintColumn.text = WithoutMatchingLine(text, lockLookup);
            }

            tintColumn.ForceMeshUpdate();
            if (lockRow == null)
                return;
            lockRow.SetActive(show);
            if (!show)
                return;
            if (TryFindLine(tintColumn, lockLookup, out character))
                PlaceCircle(lockRow.GetComponent<RectTransform>(), character);
        }

        static string MatchingLine(string text, string lookup)
        {
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (LineMatches(lines[i], lookup))
                    return lines[i].Trim();
            }

            return null;
        }

        static string WithoutMatchingLine(string text, string lookup)
        {
            string[] lines = text.Split('\n');
            var kept = new List<string>(lines.Length);
            for (int i = 0; i < lines.Length; i++)
            {
                if (LineMatches(lines[i], lookup))
                    continue;
                kept.Add(lines[i]);
            }

            return string.Join("\n", kept);
        }

        static bool LineMatches(string line, string lookup)
        {
            if (string.IsNullOrWhiteSpace(lookup))
                return false;
            line = line.Trim();
            lookup = lookup.Trim();
            return line == lookup || line.StartsWith(lookup, System.StringComparison.Ordinal);
        }

        bool TryFindLine(TMP_Text column, string label, out TMP_CharacterInfo character)
        {
            character = default;
            column.ForceMeshUpdate();
            TMP_TextInfo info = column.textInfo;
            if (info == null || info.lineCount == 0)
                return false;

            var text = new StringBuilder();
            for (int line = 0; line < info.lineCount; line++)
            {
                int first = info.lineInfo[line].firstCharacterIndex;
                int last = info.lineInfo[line].lastCharacterIndex;
                if (first < 0 || last < first || last >= info.characterCount)
                    continue;
                text.Length = 0;
                for (int c = first; c <= last; c++)
                {
                    char ch = info.characterInfo[c].character;
                    if (ch == '\0' || ch == '\n' || ch == '\r')
                        continue;
                    text.Append(ch);
                }

                if (!LineMatches(text.ToString(), label))
                    continue;
                character = info.characterInfo[first];
                return true;
            }

            return false;
        }

        static void PlaceCircle(RectTransform rect, TMP_CharacterInfo character)
        {
            float midY = (character.ascender + character.descender) * 0.5f;
            rect.anchoredPosition = new Vector2(character.origin - 48f, midY);
        }

        void ToggleSamePalette()
        {
            samePalette = !samePalette;
            if (paletteDot != null)
                paletteDot.enabled = samePalette;
            PlayerPrefs.SetInt(PaletteKey, samePalette ? 1 : 0);
            PlayerPrefs.Save();
        }

        void ChooseTintMode(int mode)
        {
            if (TintStyle.Mode == mode)
                return;
            TintStyle.SetMode(mode);
            SyncTintDots();
            // 模式只是存了起来，已选中的衣服要主动重画一次才会变。
            UnityEngine.Object.FindAnyObjectByType<LayerColorController>()?.ReapplyToTarget();
        }

        void ToggleGradKind(int kind)
        {
            int next = TintStyle.GradKind == kind ? TintStyle.GradNone : kind;
            TintStyle.SetGradKind(next);
            if (next == TintStyle.GradNone)
                TintStyle.ReleaseOriginal();
            SyncTintDots();
            LayerColorController controller = UnityEngine.Object.FindAnyObjectByType<LayerColorController>();
            controller?.ShowOriginalGradient();
        }

        /// <summary>清除按钮把 TintStyle 清掉后，把圆点画回当前状态。</summary>
        public void SyncTintDots()
        {
            if (curveDot != null)
                curveDot.enabled = TintStyle.Mode == TintStyle.CurveMode;
            if (hueDot != null)
                hueDot.enabled = TintStyle.Mode == TintStyle.Hue;
            if (verticalDot != null)
                verticalDot.enabled = TintStyle.GradKind == TintStyle.GradVertical;
            if (lumaDot != null)
                lumaDot.enabled = TintStyle.GradKind == TintStyle.GradLuma;
            if (lockDot != null)
                lockDot.enabled = lockGradient;
            RefreshLockRow();
        }

        int BuildColumn(string objectName, bool[] values, UnityEngine.Events.UnityAction save)
        {
            Transform column = transform.Find(objectName);
            TMP_Text columnLabel = column != null ? column.GetComponent<TMP_Text>() : null;
            if (columnLabel == null)
                return 0;

            columnLabel.raycastTarget = false;
            columnLabel.ForceMeshUpdate();
            TMP_TextInfo info = columnLabel.textInfo;
            if (info == null || info.lineCount == 0)
                return 0;

            int placed = 0;
            var text = new StringBuilder();
            for (int line = 0; line < info.lineCount; line++)
            {
                int first = info.lineInfo[line].firstCharacterIndex;
                int last = info.lineInfo[line].lastCharacterIndex;
                if (first < 0 || last < first || last >= info.characterCount)
                    continue;

                text.Length = 0;
                for (int c = first; c <= last; c++)
                {
                    char ch = info.characterInfo[c].character;
                    if (ch == '\0' || ch == '\n' || ch == '\r')
                        continue;
                    text.Append(ch);
                }

                string label = text.ToString().Trim();
                // 场景文案可写「头发组」「衣裤组」，匹配时去掉「组」。
                string key = NormalizeOptionLabel(label);
                if (key == "衣裤" || key == "服装")
                {
                    CreateCircle(columnLabel, objectName, OutfitGroups, values, save, info.characterInfo[first]);
                    placed++;
                    continue;
                }

                if (key == "鞋袜")
                {
                    CreateCircle(columnLabel, objectName, FootwearGroups, values, save, info.characterInfo[first]);
                    placed++;
                    continue;
                }

                int which = IndexOfOption(key);
                if (which < 0 || which >= values.Length || which == CoatGroup)
                    continue;

                CreateCircle(columnLabel, objectName, new[] { which }, values, save, info.characterInfo[first]);
                placed++;
            }

            return placed;
        }

        static string NormalizeOptionLabel(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;
            text = text.Trim();
            if (text.EndsWith("组", System.StringComparison.Ordinal))
                text = text.Substring(0, text.Length - 1).Trim();
            return text;
        }

        static int IndexOfOption(string text)
        {
            text = NormalizeOptionLabel(text);
            for (int i = 0; i < Options.Length; i++)
            {
                if (Options[i] == text)
                    return i;
            }

            return -1;
        }

        void CreateCircle(TMP_Text columnLabel, string column, int[] indices, bool[] values, UnityEngine.Events.UnityAction save, TMP_CharacterInfo character)
        {
            var used = new List<int>();
            for (int i = 0; i < indices.Length; i++)
            {
                if (indices[i] >= 0 && indices[i] < values.Length)
                    used.Add(indices[i]);
            }

            if (used.Count == 0)
                return;

            int[] groups = used.ToArray();
            var go = new GameObject(
                column + groups[0].ToString(),
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image),
                typeof(Button));
            go.transform.SetParent(columnLabel.rectTransform, false);
            MarkCircle(go);

            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(40f, 40f);
            float midY = (character.ascender + character.descender) * 0.5f;
            rect.anchoredPosition = new Vector2(character.origin - 48f, midY);

            Image image = go.GetComponent<Image>();
            image.sprite = ringSprite;
            image.color = columnLabel.color;
            image.raycastTarget = true;

            var dotGo = new GameObject("Dot", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            dotGo.transform.SetParent(go.transform, false);
            MarkCircle(dotGo);
            var dotRect = dotGo.GetComponent<RectTransform>();
            dotRect.anchorMin = dotRect.anchorMax = new Vector2(0.5f, 0.5f);
            dotRect.pivot = new Vector2(0.5f, 0.5f);
            dotRect.sizeDelta = new Vector2(20f, 20f);
            dotRect.anchoredPosition = Vector2.zero;
            Image dot = dotGo.GetComponent<Image>();
            dot.sprite = dotSprite;
            dot.color = columnLabel.color;
            dot.raycastTarget = false;
            bool on = true;
            for (int i = 0; i < groups.Length; i++)
                on &= values[groups[i]];
            dot.enabled = on;

            Button button = go.GetComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = image;
            int[] captured = groups;
            button.onClick.AddListener(() =>
            {
                bool next = false;
                for (int i = 0; i < captured.Length; i++)
                    next |= !values[captured[i]];
                for (int i = 0; i < captured.Length; i++)
                    values[captured[i]] = next;
                dot.enabled = next;
                save();
            });
        }

        void LoadChecks()
        {
            LoadMask(ColorKey, colorChecks, false);
            if (!PlayerPrefs.HasKey(ColorKey))
            {
                colorChecks[0] = true;
                SaveColor();
            }

            LoadMask(PartKey, partChecks, true);
            if (!PlayerPrefs.HasKey(PartKey))
                SavePart();

            LoadMask(LinkKey, linkChecks, false);
            if (!PlayerPrefs.HasKey(LinkKey))
            {
                linkChecks[0] = true;
                SaveLink();
            }

            // 首次进入：色系保持一致默认勾选。
            if (!PlayerPrefs.HasKey(PaletteKey))
            {
                samePalette = true;
                PlayerPrefs.SetInt(PaletteKey, 1);
                PlayerPrefs.Save();
            }
            else
                samePalette = PlayerPrefs.GetInt(PaletteKey, 1) != 0;
            lockGradient = PlayerPrefs.GetInt(LockKey, 0) != 0;
            if (PlayerPrefs.GetInt(CoatKey, 0) == 0)
            {
                colorChecks[CoatGroup] = colorChecks[1];
                partChecks[CoatGroup] = partChecks[1];
                linkChecks[CoatGroup] = linkChecks[1];
                PlayerPrefs.SetInt(CoatKey, 1);
                SaveColor();
                SavePart();
                SaveLink();
            }
        }

        static void LoadMask(string key, bool[] values, bool fallbackOn)
        {
            int mask = PlayerPrefs.GetInt(key, fallbackOn ? (1 << values.Length) - 1 : 0);
            for (int i = 0; i < values.Length; i++)
                values[i] = (mask & (1 << i)) != 0;
        }

        void SaveColor() => SaveMask(ColorKey, colorChecks);

        void SavePart() => SaveMask(PartKey, partChecks);

        void SaveLink() => SaveMask(LinkKey, linkChecks);

        static void SaveMask(string key, bool[] values)
        {
            int mask = 0;
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i])
                    mask |= 1 << i;
            }

            PlayerPrefs.SetInt(key, mask);
            PlayerPrefs.Save();
        }

        static Sprite MakeDisc(bool ring)
        {
            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            float center = (size - 1) * 0.5f;
            const float outer = 28f;
            float inner = ring ? 21f : 0f;
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float distance = Vector2.Distance(new Vector2(x, y), new Vector2(center, center));
                    byte alpha = distance <= outer && distance >= inner ? (byte)255 : (byte)0;
                    pixels[y * size + x] = new Color32(255, 255, 255, alpha);
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        }

        void IgnoreTitleClicks()
        {
            Transform title = transform.Find("title");
            TMP_Text titleText = title != null ? title.GetComponent<TMP_Text>() : null;
            if (titleText != null)
                titleText.raycastTarget = false;
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
