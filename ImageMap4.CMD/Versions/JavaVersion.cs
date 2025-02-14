﻿using fNbt;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ImageMap4;

public interface IJavaVersion
{
    ReadOnlyMemory<Color> GetPalette();
    Image<Rgba32> Decode(byte[] colors);
    byte[] EncodeColors(Image<Rgba32> image);
    NbtCompound CreateMapCompound(MapData map);
    NbtCompound MakeMapItem(long id);
    NbtCompound MakeStructureItem(StructureGrid structure);
    bool StructuresSupported { get; }
    NbtCompound CreateStructureFile(StructureGrid structure);
    string StructureFileLocation(string world_folder, string identifier);
}

public class JavaVersionBuilder
{
    public readonly List<Rgba32> BaseColors = new();
    public byte[]? Multipliers;
    public NbtCompound? MapEntity;
    public NbtCompound? MapData;
    public NbtCompound? MapItem;
    public NbtCompound? StructureItem;
    public bool StructuresSupported = false;
    public string? StructureFolder;
    public string? Name;
    public void Add(JavaUpdate update)
    {
        if (update.SetBaseColors != null)
        {
            this.BaseColors.Clear();
            this.BaseColors.AddRange(update.SetBaseColors);
        }
        if (update.AddBaseColors != null)
            this.BaseColors.AddRange(update.AddBaseColors);
        this.Multipliers = update.Multipliers ?? this.Multipliers;
        this.MapEntity = update.MapEntity ?? this.MapEntity;
        this.MapData = update.MapData ?? this.MapData;
        this.MapItem = update.MapItem ?? this.MapItem;
        this.StructureItem = update.StructureItem ?? this.StructureItem;
        this.Name = update.Name ?? this.Name;
        this.StructureFolder = update.StructureFolder ?? this.StructureFolder;
        this.StructuresSupported |= update.StructuresSupported ?? false;
    }
    public IJavaVersion Build()
    {
        var mp = MapItem;
        NbtCompound item_maker(long id)
        {
            var compound = (NbtCompound)mp.Clone();
            foreach (var item in compound.GetAllTags().OfType<NbtString>())
            {
                if (item.Value == "@s")
                    item.Parent[item.Name] = new NbtShort((short)id);
                else if (item.Value == "@i")
                    item.Parent[item.Name] = new NbtInt((int)id);
            }
            return compound;
        }
        var si = StructureItem;
        var sf = StructureFolder;
        NbtCompound structure_maker(StructureGrid structure)
        {
            var compound = (NbtCompound)si.Clone();
            foreach (var item in compound.GetAllTags().OfType<NbtString>())
            {
                string identifier = structure.Identifier;
                if (sf == "structures")
                    identifier = identifier.Replace(':', '_');
                if (item.Value == "@id")
                    item.Parent[item.Name] = new NbtString(identifier);
                else if (item.Value == "@old_name")
                    item.Parent[item.Name] = new NbtString($"§r§d{identifier}§r");
                else if (item.Value == "@name")
                    item.Parent[item.Name] = new NbtString($"{{\"text\":\"{identifier}\",\"italic\":false}}");
                else if (item.Value == "@x")
                    item.Parent[item.Name] = new NbtInt(1);
                else if (item.Value == "@y")
                    item.Parent[item.Name] = new NbtInt(structure.GridHeight);
                else if (item.Value == "@z")
                    item.Parent[item.Name] = new NbtInt(structure.GridWidth);
            }
            return compound;
        }
        var me = MapEntity;
        NbtCompound frame_maker(bool glowing, bool invisible)
        {
            var compound = (NbtCompound)me.Clone();
            foreach (var item in compound.GetAllTags().OfType<NbtString>())
            {
                if (item.Value == "@id")
                    item.Parent[item.Name] = new NbtString(glowing ? "minecraft:glow_item_frame" : "minecraft:item_frame");
                else if (item.Value == "@invisible")
                    item.Parent[item.Name] = new NbtByte(invisible);
            }
            return compound;
        }
        var md = MapData;
        NbtCompound data_maker(MapData data)
        {
            var compound = (NbtCompound)md.Clone();
            foreach (var item in compound.GetAllTags().OfType<NbtString>())
            {
                if (item.Value == "@colors")
                    item.Parent[item.Name] = new NbtByteArray(data.Colors);
            }
            return compound;
        }
        string structure_folder(string world, string identifier)
        {
            if (sf == "structures")
                return Path.Combine(world, "structures", identifier.Replace(':', '_') + ".nbt");
            int colon = identifier.IndexOf(':');
            return Path.Combine(world, sf, identifier[..colon], "structures", identifier[(colon + 1)..] + ".nbt");
        }
        return new JavaVersion(Name, GetPalette())
        {
            MapMaker = item_maker,
            FrameMaker = frame_maker,
            DataMaker = data_maker,
            StructureMaker = structure_maker,
            StructurePath = structure_folder,
            StructuresSupported = StructuresSupported
        };
    }

    private IEnumerable<Color> GetPalette()
    {
        foreach (var color in BaseColors)
        {
            var alts = GetAlternateColors(color);
            foreach (var alt in alts)
            {
                yield return alt;
            }
        }
    }

    private IEnumerable<Rgba32> GetAlternateColors(Rgba32 color)
    {
        return Multipliers.Select(x => Multiply(color, x));
    }

    private static Rgba32 Multiply(Rgba32 color, byte value)
    {
        return new Rgba32((byte)(color.R * value / 255), (byte)(color.G * value / 255), (byte)(color.B * value / 255), color.A);
    }
}

public delegate NbtCompound MapDataMaker(MapData data);
public delegate NbtCompound MapItemMaker(long id);
public delegate NbtCompound StructureItemMaker(StructureGrid structure);
public delegate NbtCompound ItemFrameMaker(bool glowing, bool invisible);
public delegate string StructurePathGetter(string world, string identifier);

public class JavaVersion : IJavaVersion
{
    private readonly Color[] Palette;
    private readonly Dictionary<byte, Rgba32> ColorMap = new();
    private readonly Dictionary<Color, byte> ReverseColorMap = new();
    public MapItemMaker MapMaker { get; init; }
    public StructureItemMaker StructureMaker { get; init; }
    public ItemFrameMaker FrameMaker { get; init; }
    public MapDataMaker DataMaker { get; init; }
    public StructurePathGetter StructurePath { get; init; }
    public string Name { get; init; }
    public bool StructuresSupported { get; init; }
    public JavaVersion(string name, IEnumerable<Color> palette)
    {
        Name = name;
        Palette = palette.ToArray();
        for (byte i = 0; i < Palette.Length; i++)
        {
            ColorMap[i] = Palette[i];
            if (!ReverseColorMap.ContainsKey(Palette[i]))
                ReverseColorMap[Palette[i]] = i;
        }
    }

    public ReadOnlyMemory<Color> GetPalette() => Palette;

    public Image<Rgba32> Decode(byte[] colors)
    {
        byte[] pixels = new byte[128 * 128 * 4];
        for (int i = 0; i < colors.Length; i++)
        {
            if (ColorMap.TryGetValue(colors[i], out var color))
            {
                pixels[4 * i] = color.R;
                pixels[4 * i + 1] = color.G;
                pixels[4 * i + 2] = color.B;
                pixels[4 * i + 3] = color.A;
            }
        }
        return Image.LoadPixelData<Rgba32>(pixels, 128, 128);
    }

    public byte[] EncodeColors(Image<Rgba32> image)
    {
        byte[] result = new byte[image.Width * image.Height];
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                var b = image[x, y];
                result[y * image.Height + x] = ReverseColorMap[b];
            }
        }
        return result;
    }

    public NbtCompound CreateStructureFile(StructureGrid structure)
    {
        var mapids = structure.ToIDGrid();
        var entities = new NbtList("entities");
        for (int y = 0; y < structure.GridHeight; y++)
        {
            for (int x = 0; x < structure.GridWidth; x++)
            {
                long? val = mapids[x, y];
                if (val.HasValue)
                {
                    var frame = FrameMaker(structure.GlowingFrames, structure.InvisibleFrames);
                    frame.Name = "nbt";
                    var item = MapMaker(val.Value);
                    item.Name = "Item";
                    frame.Add(item);
                    entities.Add(new NbtCompound()
                    {
                        frame,
                        new NbtList("blockPos") { new NbtInt(0), new NbtInt(structure.GridHeight - y - 1), new NbtInt(x) },
                        new NbtList("pos") { new NbtDouble(0), new NbtDouble(structure.GridHeight - y - 1 + 0.5), new NbtDouble(x + 0.5) }
                    });
                }
            }
        }
        return new NbtCompound("") {
            new NbtList("size") {
                new NbtInt(1), new NbtInt(structure.GridHeight), new NbtInt(structure.GridWidth)
            },
            entities,
            new NbtList("blocks") {
                new NbtCompound() {
                    new NbtList("pos") { new NbtInt(0), new NbtInt(0), new NbtInt(0) },
                    new NbtInt("state", 0)
                }
            },
            new NbtList("palette") {
                new NbtCompound() {
                    new NbtString("Name", "minecraft:air")
                }
            }
        };
    }

    public NbtCompound CreateMapCompound(MapData map) => DataMaker(map);
    public NbtCompound MakeMapItem(long id) => MapMaker(id);
    public NbtCompound MakeStructureItem(StructureGrid structure) => StructureMaker(structure);
    public string StructureFileLocation(string world_folder, string identifier) => StructurePath(world_folder, identifier);
}
