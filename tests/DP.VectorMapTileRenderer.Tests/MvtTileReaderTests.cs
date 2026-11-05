using System.Text;
using DP.VectorMapTileRenderer.Mvt;
using Xunit;

namespace DP.VectorMapTileRenderer.Tests;

public sealed class MvtTileReaderTests
{
    [Fact]
    public void DecodesPointFeature()
    {
        var tile = MvtBuilder.Build(
            "points",
            4096,
            MvtBuilder.FeaturePoint(
                MvtBuilder.Tags((0, 0)),
                0,
                0,
                10,
                10),
            MvtBuilder.Key("name"),
            MvtBuilder.StringValue("station"));

        var decoded = MvtTileReader.Read(tile);

        var layer = Assert.Single(decoded.Layers);
        Assert.Equal("points", layer.Name);
        Assert.Equal(4096d, layer.Extent);

        var feature = Assert.Single(layer.Features);
        Assert.Equal(VectorGeometryType.Point, feature.GeometryType);
        Assert.Equal("station", feature.Attributes["name"]);
        var ring = Assert.Single(feature.Geometry);
        Assert.Equal(2, ring.Count);
        Assert.Equal(new Point(0, 0), ring[0]);
        Assert.Equal(new Point(10, 10), ring[1]);
    }

    [Fact]
    public void DecodesPolygonAndClosesRing()
    {
        var tile = MvtBuilder.Build(
            "areas",
            4096,
            MvtBuilder.FeaturePolygon(MvtBuilder.Tags(),
                MvtBuilder.Command(MvtBuilder.MoveTo, 1),
                MvtBuilder.ZigZag(0), MvtBuilder.ZigZag(0),
                MvtBuilder.Command(MvtBuilder.LineTo, 3),
                MvtBuilder.ZigZag(0), MvtBuilder.ZigZag(10),
                MvtBuilder.ZigZag(10), MvtBuilder.ZigZag(0),
                MvtBuilder.ZigZag(0), MvtBuilder.ZigZag(-10),
                MvtBuilder.Command(MvtBuilder.ClosePath, 1)));

        var decoded = MvtTileReader.Read(tile);
        var feature = Assert.Single(Assert.Single(decoded.Layers).Features);

        Assert.Equal(VectorGeometryType.Polygon, feature.GeometryType);
        var ring = Assert.Single(feature.Geometry);
        Assert.Equal(5, ring.Count);
        Assert.Equal(ring[0], ring[^1]);
    }

    [Fact]
    public void DetectsMvtEncoding()
    {
        var tile = MvtBuilder.Build("empty", 4096, MvtBuilder.FeaturePoint(MvtBuilder.Tags(), 0, 0));

        Assert.Equal(VectorTileEncoding.Mvt, VectorTileReader.Detect(tile));
        Assert.Single(VectorTileReader.Read(tile).Layers);
    }

    private static class MvtBuilder
    {
        public const int MoveTo = 1;
        public const int LineTo = 2;
        public const int ClosePath = 7;

        public static byte[] Build(string layerName, int extent, params byte[][] encodedFields)
        {
            var layer = new List<byte>();
            layer.AddRange(FieldString(1, layerName));
            foreach (var field in encodedFields)
            {
                layer.AddRange(field);
            }

            layer.AddRange(FieldVarint(5, extent));
            return FieldBytes(3, layer.ToArray());
        }

        public static byte[] FeaturePoint(byte[] tags, params int[] coordinates)
        {
            var geometry = new List<byte>();
            for (var index = 0; index + 1 < coordinates.Length; index += 2)
            {
                geometry.AddRange(Command(MoveTo, 1));
                geometry.AddRange(ZigZag(coordinates[index]));
                geometry.AddRange(ZigZag(coordinates[index + 1]));
            }

            return Feature(1, tags, geometry);
        }

        public static byte[] FeaturePolygon(byte[] tags, params byte[][] geometry)
        {
            var commands = new List<byte>();
            foreach (var item in geometry)
            {
                commands.AddRange(item);
            }

            return Feature(3, tags, commands);
        }

        public static byte[] Feature(int type, byte[] tags, IEnumerable<byte> geometry)
        {
            var feature = new List<byte>();
            feature.AddRange(FieldBytes(2, tags));
            feature.AddRange(FieldVarint(3, type));
            feature.AddRange(FieldBytes(4, geometry.ToArray()));
            return FieldBytes(2, feature.ToArray());
        }

        public static byte[] Tags(params (int Key, int Value)[] pairs)
        {
            var tags = new List<byte>();
            foreach (var (key, value) in pairs)
            {
                tags.AddRange(Varint((ulong)key));
                tags.AddRange(Varint((ulong)value));
            }

            return tags.ToArray();
        }

        public static byte[] Key(string key) => FieldString(3, key);

        public static byte[] StringValue(string value) => FieldBytes(4, FieldString(1, value));

        public static byte[] Command(int command, int count) => Varint((ulong)((count << 3) | command));

        public static byte[] ZigZag(int value) => Varint((ulong)((value << 1) ^ (value >> 31)));

        private static byte[] FieldString(int field, string value) =>
            FieldBytes(field, Encoding.UTF8.GetBytes(value));

        private static byte[] FieldBytes(int field, byte[] payload)
        {
            var result = new List<byte>();
            result.AddRange(Varint((ulong)((field << 3) | 2)));
            result.AddRange(Varint((ulong)payload.Length));
            result.AddRange(payload);
            return result.ToArray();
        }

        private static byte[] FieldVarint(int field, int value)
        {
            var result = new List<byte>();
            result.AddRange(Varint((ulong)((field << 3) | 0)));
            result.AddRange(Varint((ulong)value));
            return result.ToArray();
        }

        private static byte[] Varint(ulong value)
        {
            var result = new List<byte>();
            do
            {
                var current = (byte)(value & 0x7F);
                value >>= 7;
                if (value != 0)
                {
                    current |= 0x80;
                }

                result.Add(current);
            }
            while (value != 0);

            return result.ToArray();
        }
    }
}
