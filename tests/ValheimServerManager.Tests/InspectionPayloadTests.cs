using System.Text.Json;
using System.Text.Json.Nodes;
using ValheimServerManager.Services;
using Xunit;

namespace ValheimServerManager.Tests;

public sealed class InspectionPayloadTests
{
    private static JsonObject Sample() => JsonNode.Parse("""
        {"ok":true,"character":{"id":"42","name":"Eirik","biome":"Meadows",
        "health":100,"maxHealth":100,"stamina":50,"maxStamina":50,"eitr":0,"maxEitr":0,
        "armor":20,"weight":30,"inventoryWidth":8,"inventoryHeight":4,
        "skills":[{"name":"Run","level":12}]},"items":[],"icons":{}}
        """)!.AsObject();

    [Fact]
    public void ValidEmptyInventoryIsAccepted()
        => Assert.True(InventoryInspectionService.IsValidSnapshot(JsonSerializer.SerializeToElement(Sample())));

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("{\"ok\":true,\"character\":{},\"items\":[]}")]
    public void InvalidShapesAreRejected(string json)
        => Assert.False(InventoryInspectionService.IsValidSnapshot(JsonSerializer.Deserialize<JsonElement>(json)));

    [Fact]
    public void NestedMalformedFieldsAreRejected()
    {
        var sample = Sample();
        sample["character"]!["skills"]![0]!["name"] = new JsonObject();
        Assert.False(InventoryInspectionService.IsValidSnapshot(JsonSerializer.SerializeToElement(sample)));
        sample = Sample(); sample["icons"] = null;
        Assert.False(InventoryInspectionService.IsValidSnapshot(JsonSerializer.SerializeToElement(sample)));
        sample = Sample(); sample["items"] = new JsonArray((JsonNode?)null);
        Assert.False(InventoryInspectionService.IsValidSnapshot(JsonSerializer.SerializeToElement(sample)));
        sample = Sample(); sample["character"]!["health"] = "NaN";
        Assert.False(InventoryInspectionService.IsValidSnapshot(JsonSerializer.SerializeToElement(sample)));
    }
}
