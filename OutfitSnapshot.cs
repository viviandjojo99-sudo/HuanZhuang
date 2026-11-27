using System;
using System.Collections.Generic;

namespace DressUp.Avatar
{
    [Serializable]
    public class LayerColorEntry
    {
        public string layerName;
        public float r = 1f;
        public float g = 1f;
        public float b = 1f;
        public float a = 1f;
        public float lift;
        public int hasEnd;
        public float er = 1f;
        public float eg = 1f;
        public float eb = 1f;

        public UnityEngine.Color ToColor() => new UnityEngine.Color(r, g, b, a);

        public UnityEngine.Color EndColor() => new UnityEngine.Color(er, eg, eb, 1f);

        public static LayerColorEntry From(string layerName, UnityEngine.Color color)
        {
            return new LayerColorEntry
            {
                layerName = layerName,
                r = color.r,
                g = color.g,
                b = color.b,
                a = color.a
            };
        }
    }

    [Serializable]
    public class EquippedSlot
    {
        public string layerName;
        public string itemId;
    }

    [Serializable]
    public class OutfitSnapshot
    {
        public string characterId;
        public List<EquippedSlot> slots = new List<EquippedSlot>();
        public List<LayerColorEntry> layerColors = new List<LayerColorEntry>();

        // 玩家在「图层顺序」页拖过之后才有内容（图层名，自下而上）。
        // 空 = 没调过，沿用场景里的默认层叠。旧存档没有这个字段，读出来就是空。
        public List<string> layerOrder = new List<string>();
    }
}
