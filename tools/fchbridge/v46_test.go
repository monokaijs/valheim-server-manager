package main

import (
	"bytes"
	"crypto/sha512"
	"encoding/base64"
	"encoding/binary"
	"testing"

	"github.com/lanchelms/fch-decoder/valheim"
)

func version46Fixture(t *testing.T) []byte {
	t.Helper()
	var player bytes.Buffer
	_ = binary.Write(&player, binary.LittleEndian, uint32(33))
	player.Write(make([]byte, 16))
	writeV46String(&player, "")
	player.Write(make([]byte, 4))
	_ = binary.Write(&player, binary.LittleEndian, uint32(109))
	_ = binary.Write(&player, binary.LittleEndian, uint16(1))
	entry := v46Item{value: valheim.Item{Name: "Wood", Stack: 10, Quality: 1, Durability: 100, GridX: 0, GridY: 0, WorldLevel: 2, PickedUp: true}, hash: stableHash("Wood"), customRaw: []byte{1, 3, 'm', 'o', 'd', 4, 'k', 'e', 'e', 'p'}, cheated: 0x80}
	if err := writeV46Item(&player, entry); err != nil {
		t.Fatal(err)
	}
	player.Write([]byte{0xde, 0xad, 0xbe, 0xef}) // Opaque later player fields.

	var payload bytes.Buffer
	_ = binary.Write(&payload, binary.LittleEndian, uint32(46))
	_ = binary.Write(&payload, binary.LittleEndian, uint32(2))
	_ = binary.Write(&payload, binary.LittleEndian, uint32(1))
	payload.Write(make([]byte, 8))
	payload.Write(make([]byte, 9*4)) // Empty statistics dictionaries.
	payload.WriteByte(0)
	payload.Write(make([]byte, 4)) // No worlds.
	writeV46String(&payload, "Viking")
	payload.Write(make([]byte, 8))
	writeV46String(&payload, "")
	payload.Write(make([]byte, 9))
	payload.WriteByte(1)
	_ = binary.Write(&payload, binary.LittleEndian, uint32(player.Len()))
	payload.Write(player.Bytes())

	raw := binary.LittleEndian.AppendUint32(nil, uint32(payload.Len()))
	raw = append(raw, payload.Bytes()...)
	raw = binary.LittleEndian.AppendUint32(raw, 64)
	digest := sha512.Sum512(payload.Bytes())
	return append(raw, digest[:]...)
}

func TestVersion46InventoryEditPreservesOpaqueData(t *testing.T) {
	original := version46Fixture(t)
	source, err := parseV46(original)
	if err != nil {
		t.Fatal(err)
	}
	if source.entries[0].value.Name != "Wood" || source.entries[0].value.Stack != 10 {
		t.Fatalf("unexpected item: %+v", source.entries[0].value)
	}
	result, err := invoke(t, map[string]any{"action": "replace", "profile": base64.StdEncoding.EncodeToString(original), "x": 0, "y": 0,
		"item": map[string]any{"name": "Wood", "stack": 20, "quality": 1, "durability": 85.5, "equipped": true}})
	if err != nil {
		t.Fatal(err)
	}
	edited, err := base64.StdEncoding.DecodeString(result.Profile)
	if err != nil {
		t.Fatal(err)
	}
	target, err := parseV46(edited)
	if err != nil {
		t.Fatal(err)
	}
	got := target.entries[0]
	if got.value.Stack != 20 || got.value.Durability != 85.5 || !got.value.Equipped {
		t.Fatalf("unexpected edited item: %+v", got.value)
	}
	if !bytes.Equal(got.customRaw, source.entries[0].customRaw) || got.cheated != source.entries[0].cheated {
		t.Fatal("item metadata changed")
	}
	if !bytes.Equal(source.payload[:source.playerLengthOffset], target.payload[:target.playerLengthOffset]) || !bytes.Equal(source.player[:source.inventoryStart], target.player[:target.inventoryStart]) || !bytes.Equal(source.player[source.inventoryEnd:], target.player[target.inventoryEnd:]) {
		t.Fatal("non-inventory data changed")
	}
}

func TestVersion46GiveAndRemove(t *testing.T) {
	raw := version46Fixture(t)
	given, err := invoke(t, map[string]any{"action": "give", "profile": base64.StdEncoding.EncodeToString(raw), "prefab": "Wood", "quantity": 2, "quality": 1})
	if err != nil {
		t.Fatal(err)
	}
	if given.Given != 2 || len(given.Items) != 2 {
		t.Fatalf("unexpected delivery: %+v", given)
	}
	edited, err := base64.StdEncoding.DecodeString(given.Profile)
	if err != nil {
		t.Fatal(err)
	}
	decoded, err := parseV46(edited)
	if err != nil {
		t.Fatal(err)
	}
	if decoded.entries[1].value.Name != "Wood" || decoded.entries[1].value.GridX != 1 {
		t.Fatal("new item not in first free slot")
	}
	removed, err := invoke(t, map[string]any{"action": "remove", "profile": given.Profile, "x": 1, "y": 0})
	if err != nil {
		t.Fatal(err)
	}
	if len(removed.Items) != 1 {
		t.Fatalf("unexpected item count: %d", len(removed.Items))
	}
}
