package main

import (
 "encoding/base64"
 "encoding/json"
 "strings"
 "testing"

 fch "github.com/lanchelms/fch-decoder"
 "github.com/lanchelms/fch-decoder/valheim"
)

func invoke(t *testing.T, input any) (response, error) {
 t.Helper()
 raw, err := json.Marshal(input)
 if err != nil { t.Fatal(err) }
 return run(strings.NewReader(string(raw)))
}

func fixture(t *testing.T) string {
 t.Helper()
 character := valheim.NewCharacter("Viking", 42)
 character.Player.Inventory = []valheim.Item{{Name: "Wood", Stack: 10, Quality: 1, GridX: 0, GridY: 0, CustomData: []valheim.TextEntry{{Key: "mod-key", Value: "keep-me"}}}}
 data, err := fch.EncodeBytes(character)
 if err != nil { t.Fatal(err) }
 return base64.StdEncoding.EncodeToString(data)
}

func TestReplacePreservesItemCustomData(t *testing.T) {
 result, err := invoke(t, map[string]any{"action": "replace", "profile": fixture(t), "x": 0, "y": 0,
  "item": map[string]any{"name": "Wood", "stack": 20, "quality": 1, "durability": 100, "variant": 0}})
 if err != nil { t.Fatal(err) }
 encoded, err := base64.StdEncoding.DecodeString(result.Profile)
 if err != nil { t.Fatal(err) }
 character, err := fch.DecodeBytes(encoded)
 if err != nil { t.Fatal(err) }
 if got := character.Player.Inventory[0]; got.Stack != 20 || len(got.CustomData) != 1 || got.CustomData[0].Value != "keep-me" {
  t.Fatalf("edited item lost data: %+v", got)
 }
}

func TestUnsupportedProfileIsRejected(t *testing.T) {
 _, err := invoke(t, map[string]any{"action": "inspect", "profile": "not-base64"})
 if err == nil { t.Fatal("expected invalid profile error") }
}

func TestCustomItemUsesConfiguredStackLimit(t *testing.T) {
 result, err := invoke(t, map[string]any{"action": "give", "profile": fixture(t), "prefab": "ModdedCrystal",
  "quantity": 25, "quality": 1, "maxStack": 10})
 if err != nil { t.Fatal(err) }
 if result.Given != 25 || len(result.Items) != 4 || result.Items[1].Stack != 10 || result.Items[3].Stack != 5 {
  t.Fatalf("unexpected delivery: %+v", result)
 }
}
