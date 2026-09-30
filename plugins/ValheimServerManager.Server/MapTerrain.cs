using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace ValheimServerManager.Server;

public sealed partial class ServerPlugin
{
    private const int MapSize = 2048, TileSize = 256;
    private const float MapPixelSize = 12, MapExtent = MapSize * MapPixelSize / 2;
    private WorldGenerator _mapGenerator;
    private string _mapWorld = "";
    private volatile string _mapTerrainMessage;
    private volatile ConcurrentDictionary<int, string> _mapTileMessages = new();
    private bool _mapFailed;
    private string _terrainRevision = "";
    private int _terrainGeneration, _sampleIndex, _tileOrderIndex;
    private bool _preview = true;
    private byte[] _sampleBiomes, _sampleForests;
    private float[] _sampleHeights;
    // Start near spawn so useful centre tiles arrive first, then cover the entire native map extent.
    private static readonly int[] TileOrder = Enumerable.Range(0, 64).OrderBy(i => Math.Pow(i % 8 - 3.5, 2) + Math.Pow(i / 8 - 3.5, 2)).ThenBy(i => i).ToArray();
    private static float MapWaterLevel() => ZoneSystem.instance?.m_waterLevel ?? 30;
    private void StartTerrainBuffer()
    {
        _sampleIndex = 0;
        _sampleBiomes = new byte[TileSize * TileSize]; _sampleForests = new byte[TileSize * TileSize]; _sampleHeights = new float[TileSize * TileSize];
    }
    private void TickTerrain(WorldGenerator generator, string world)
    {
        if (!ReferenceEquals(generator, _mapGenerator) || _mapWorld != world)
        {
            _mapGenerator = generator; _mapWorld = world; _terrainRevision = Guid.NewGuid().ToString("N"); System.Threading.Interlocked.Increment(ref _terrainGeneration); _mapFailed = false;
            _preview = true; _tileOrderIndex = 0; _mapTerrainMessage = null;
            _mapTileMessages = new ConcurrentDictionary<int, string>(); StartTerrainBuffer();
        }
        if (ZoneSystem.instance == null || _mapFailed || _tileOrderIndex >= 64) return;
        var budget = Stopwatch.StartNew();
        try
        {
            var tile = TileOrder[_tileOrderIndex]; var step = _preview ? MapSize / (float)TileSize * MapPixelSize : MapPixelSize;
            for (var n = 0; n < 128 && _sampleIndex < _sampleBiomes.Length && budget.Elapsed.TotalMilliseconds < 2; n++, _sampleIndex++)
            {
                var px = _sampleIndex % TileSize; var py = _sampleIndex / TileSize;
                var x = -MapExtent + ((_preview ? 0 : tile % 8 * TileSize) + px + .5f) * step;
                var z = MapExtent - ((_preview ? 0 : tile / 8 * TileSize) + py + .5f) * step;
                var biome = generator.GetBiome(x, z);
                var height = generator.GetBiomeHeight(biome, x, z, out var mask);
                _sampleHeights[_sampleIndex] = height;
                _sampleBiomes[_sampleIndex] = BiomeIndex(biome);
                var forest = biome == Heightmap.Biome.BlackForest ? 1f : biome == Heightmap.Biome.Meadows ? WorldGenerator.InForest(new Vector3(x, 0, z)) ? 1 : 0 : biome == Heightmap.Biome.Plains ? WorldGenerator.GetForestFactor(new Vector3(x, 0, z)) < .8f ? 1 : 0 : biome == Heightmap.Biome.Mistlands ? MistForest(WorldGenerator.GetForestFactor(new Vector3(x, 0, z))) : 0;
                _sampleForests[_sampleIndex] = height < MapWaterLevel() ? (byte)0 : (byte)(Mathf.Clamp01(forest) * 255);
            }
            if (_sampleIndex != _sampleBiomes.Length) return;
            var heights = _sampleHeights; var biomes = _sampleBiomes; var forests = _sampleForests;
            var generation = _terrainGeneration; var revision = _terrainRevision; var preview = _preview; var name = generator.m_world.m_name; var water = MapWaterLevel(); var tiles = _mapTileMessages;
            // Pure serialization/compression work runs off-thread, never invokes game APIs.
            Task.Run(() =>
            {
                var bytes = new byte[heights.Length * sizeof(float)]; Buffer.BlockCopy(heights, 0, bytes, 0, bytes.Length);
                if (!BitConverter.IsLittleEndian) for (var i = 0; i < bytes.Length; i += 4) Array.Reverse(bytes, i, 4);
                var payload = new { worldId = world, revision, name, size = TileSize, worldSize = MapSize, extent = MapExtent, x = tile % 8, y = tile / 8, biomes = Convert.ToBase64String(biomes), heights = Convert.ToBase64String(bytes), forests = Convert.ToBase64String(forests), waterLevel = water };
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(new { type = preview ? "mapTerrain" : "mapTile", payload });
                if (generation != System.Threading.Volatile.Read(ref _terrainGeneration)) return;
                if (preview) _mapTerrainMessage = json; else tiles[tile] = json;
            });
            if (_preview) _preview = false; else _tileOrderIndex++;
            if (_tileOrderIndex < 64) StartTerrainBuffer();
        }
        catch (Exception error) { _mapFailed = true; Logger.LogWarning("Map generation unavailable: " + error.Message); }
    }
    private static float MistForest(float factor) { var t = Mathf.InverseLerp(1.1f, 1.3f, factor); return 1 - t * t * (3 - 2 * t); }
    private static byte BiomeIndex(Heightmap.Biome biome) => biome switch
    {
        Heightmap.Biome.Meadows => 0, Heightmap.Biome.Swamp => 1, Heightmap.Biome.Mountain => 2,
        Heightmap.Biome.BlackForest => 3, Heightmap.Biome.Plains => 4, Heightmap.Biome.AshLands => 5,
        Heightmap.Biome.DeepNorth => 6, Heightmap.Biome.Ocean => 7, Heightmap.Biome.Mistlands => 8, _ => 7
    };
}
