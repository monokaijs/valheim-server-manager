package main

import (
	"bytes"
	"crypto/sha512"
	"encoding/base64"
	"encoding/binary"
	"errors"
	"fmt"
	"math"
	"strings"
	"unicode/utf16"

	"github.com/lanchelms/fch-decoder/valheim"
	"github.com/lanchelms/fch-decoder/valheim/items"
)

// Version 46 keeps the character's statistics and world data outside the
// length-prefixed player payload. Only the inventory slice is rewritten here;
// all other bytes, including mod data and fields added after inventory, remain
// exactly as saved by Valheim.
type v46Reader struct {
	data []byte
	pos  int
}

func (r *v46Reader) take(n int) []byte {
	if n < 0 || n > len(r.data)-r.pos {
		panic(errors.New("truncated version 46 character save"))
	}
	part := r.data[r.pos : r.pos+n]
	r.pos += n
	return part
}
func (r *v46Reader) byte() byte  { return r.take(1)[0] }
func (r *v46Reader) u16() uint16 { return binary.LittleEndian.Uint16(r.take(2)) }
func (r *v46Reader) u32() uint32 { return binary.LittleEndian.Uint32(r.take(4)) }
func (r *v46Reader) string() string {
	var length uint32
	for shift := uint(0); shift < 35; shift += 7 {
		b := r.byte()
		if shift == 28 && b > 0x0f {
			panic(errors.New("invalid character string length"))
		}
		length |= uint32(b&0x7f) << shift
		if b&0x80 == 0 {
			return string(r.take(int(length)))
		}
	}
	panic(errors.New("invalid character string length"))
}
func (r *v46Reader) count(limit int) int {
	n := r.u32()
	if n > uint32(limit) || int(n) > len(r.data)-r.pos {
		panic(errors.New("invalid character collection length"))
	}
	return int(n)
}
func (r *v46Reader) dictionary() {
	for i, count := 0, r.count(100000); i < count; i++ {
		r.string()
		r.take(4)
	}
}
func (r *v46Reader) itemCount() int {
	first := r.byte()
	if first&0x80 == 0 {
		return int(first)
	}
	return int(first&0x7f)<<8 | int(r.byte())
}

type v46Item struct {
	value     valheim.Item
	hash      int32
	flags     byte
	cheated   byte
	customRaw []byte
	raw       []byte
}
type v46Profile struct {
	raw                []byte
	payload            []byte
	player             []byte
	name               string
	playerLengthOffset int
	playerEnd          int
	inventoryStart     int
	inventoryEnd       int
	entries            []v46Item
}

func parseV46(raw []byte) (profile *v46Profile, err error) {
	defer func() {
		if recovered := recover(); recovered != nil {
			profile = nil
			if e, ok := recovered.(error); ok {
				err = e
			} else {
				err = fmt.Errorf("invalid version 46 character: %v", recovered)
			}
		}
	}()
	if len(raw) < 80 {
		return nil, errors.New("character save is too small")
	}
	length := binary.LittleEndian.Uint32(raw[:4])
	if uint64(length)+72 != uint64(len(raw)) || binary.LittleEndian.Uint32(raw[4+length:8+length]) != 64 {
		return nil, errors.New("invalid character save length")
	}
	payload := raw[4 : 4+length]
	digest := sha512.Sum512(payload)
	if !bytes.Equal(digest[:], raw[8+length:]) {
		return nil, errors.New("invalid character save checksum")
	}
	r := &v46Reader{data: payload}
	if r.u32() != 46 {
		return nil, errors.New("unsupported character version")
	}
	stats, profiles := r.u32(), r.u32()
	if stats > 4096 || profiles > 100 || profiles == 0 {
		return nil, errors.New("invalid character statistics dimensions")
	}
	for p := uint32(0); p < profiles; p++ {
		r.take(int(stats) * 4)
		for i := 0; i < 3; i++ {
			r.dictionary()
		}
		groups := r.count(100)
		for i := 0; i < groups+5; i++ {
			r.dictionary()
		}
	}
	r.byte() // First spawn.
	worlds := r.count(10000)
	for i := 0; i < worlds; i++ {
		r.take(8 + 1 + 12 + 1 + 12 + 1 + 12 + 12)
		if r.byte() != 0 {
			r.take(int(r.u32()))
		}
	}
	name := r.string()
	r.take(8)
	r.string()    // Start seed.
	r.take(1 + 8) // Cheats and creation timestamp.
	if r.byte() == 0 {
		return nil, errors.New("character has no saved player data")
	}
	playerLengthOffset := r.pos
	playerLength := r.u32()
	player := r.take(int(playerLength))
	playerEnd := r.pos
	if r.pos != len(payload) {
		return nil, errors.New("unexpected bytes after player data")
	}
	pr := &v46Reader{data: player}
	if version := pr.u32(); version != 33 {
		return nil, fmt.Errorf("unsupported player version %d", version)
	}
	pr.take(4 * 4) // Health, stamina, and time since death.
	pr.string()    // Guardian power.
	pr.take(4)     // Cooldown.
	if version := pr.u32(); version != 109 {
		return nil, fmt.Errorf("unsupported inventory version %d", version)
	}
	inventoryStart := pr.pos
	itemCount := int(pr.u16())
	if itemCount > 4096 {
		return nil, errors.New("invalid inventory item count")
	}
	hashNames := make(map[int32]string)
	for _, metadata := range items.Catalog().List() {
		hashNames[stableHash(metadata.Name)] = metadata.Name
	}
	entries := make([]v46Item, 0, itemCount)
	for i := 0; i < itemCount; i++ {
		start := pr.pos
		durability := int32(pr.u32())
		x, y, level, flags := pr.byte(), pr.byte(), pr.byte(), pr.byte()
		item := valheim.Item{Stack: 1, Quality: 1, Durability: float32(durability) / 100, GridX: int32(x), GridY: int32(y), WorldLevel: uint32(level), PickedUp: flags&1 != 0, Equipped: flags&2 != 0}
		if flags&4 != 0 {
			item.Quality = int32(pr.u16())
		}
		if flags&8 != 0 {
			item.Stack = int32(pr.u16())
		}
		if flags&16 != 0 {
			item.Variant = int32(pr.u32())
		}
		if flags&32 != 0 {
			item.CrafterID = uint64(binary.LittleEndian.Uint64(pr.take(8)))
			item.CrafterName = pr.string()
		}
		var hash int32
		if flags&64 != 0 {
			hash = int32(pr.u32())
		}
		if name, ok := hashNames[hash]; ok {
			item.Name = name
		} else {
			item.Name = fmt.Sprintf("hash:%d", hash)
		}
		customStart := pr.pos
		if flags&128 != 0 {
			count := pr.itemCount()
			if count > 4096 {
				return nil, errors.New("invalid item custom data count")
			}
			for j := 0; j < count; j++ {
				item.CustomData = append(item.CustomData, valheim.TextEntry{Key: pr.string(), Value: pr.string()})
			}
		}
		customRaw := append([]byte(nil), pr.data[customStart:pr.pos]...)
		cheated := pr.byte()
		entries = append(entries, v46Item{value: item, hash: hash, flags: flags, cheated: cheated, customRaw: customRaw, raw: append([]byte(nil), pr.data[start:pr.pos]...)})
	}
	return &v46Profile{raw: raw, payload: payload, player: player, name: name, playerLengthOffset: playerLengthOffset, playerEnd: playerEnd, inventoryStart: inventoryStart, inventoryEnd: pr.pos, entries: entries}, nil
}

func stableHash(name string) int32 {
	units := utf16.Encode([]rune(name))
	first, second := int32(5381), int32(5381)
	for i := 0; i < len(units); i += 2 {
		first = first*33 ^ int32(units[i])
		if i+1 < len(units) {
			second = second*33 ^ int32(units[i+1])
		}
	}
	return first + second*1566083941
}

func writeV46String(buf *bytes.Buffer, value string) {
	n := uint32(len(value))
	for n >= 0x80 {
		buf.WriteByte(byte(n) | 0x80)
		n >>= 7
	}
	buf.WriteByte(byte(n))
	buf.WriteString(value)
}
func writeV46Item(buf *bytes.Buffer, entry v46Item) error {
	item := entry.value
	if math.IsNaN(float64(item.Durability)) || math.IsInf(float64(item.Durability), 0) || item.Durability < 0 || float64(item.Durability) > float64(math.MaxInt32)/100 {
		return errors.New("invalid item durability")
	}
	if item.GridX < 0 || item.GridX > 255 || item.GridY < 0 || item.GridY > 255 || item.WorldLevel > 255 || item.Quality < 1 || item.Quality > 65535 || item.Stack < 1 || item.Stack > 65535 {
		return errors.New("invalid item fields")
	}
	_ = binary.Write(buf, binary.LittleEndian, int32(math.Round(float64(item.Durability)*100)))
	buf.WriteByte(byte(item.GridX))
	buf.WriteByte(byte(item.GridY))
	buf.WriteByte(byte(item.WorldLevel))
	flags := entry.flags
	if item.PickedUp {
		flags |= 1
	} else {
		flags &^= 1
	}
	if item.Equipped {
		flags |= 2
	} else {
		flags &^= 2
	}
	if item.Quality != 1 {
		flags |= 4
	}
	if item.Stack != 1 {
		flags |= 8
	}
	if item.Variant != 0 {
		flags |= 16
	}
	if item.CrafterID != 0 {
		flags |= 32
	}
	if entry.hash != 0 {
		flags |= 64
	}
	if len(entry.customRaw) > 0 {
		flags |= 128
	}
	buf.WriteByte(flags)
	if flags&4 != 0 {
		_ = binary.Write(buf, binary.LittleEndian, uint16(item.Quality))
	}
	if flags&8 != 0 {
		_ = binary.Write(buf, binary.LittleEndian, uint16(item.Stack))
	}
	if flags&16 != 0 {
		_ = binary.Write(buf, binary.LittleEndian, item.Variant)
	}
	if flags&32 != 0 {
		_ = binary.Write(buf, binary.LittleEndian, item.CrafterID)
		writeV46String(buf, item.CrafterName)
	}
	if flags&64 != 0 {
		_ = binary.Write(buf, binary.LittleEndian, entry.hash)
	}
	buf.Write(entry.customRaw)
	buf.WriteByte(entry.cheated)
	return nil
}

func (p *v46Profile) encode() ([]byte, error) {
	var inventory bytes.Buffer
	if len(p.entries) > 65535 {
		return nil, errors.New("inventory has too many items")
	}
	_ = binary.Write(&inventory, binary.LittleEndian, uint16(len(p.entries)))
	for _, entry := range p.entries {
		if entry.raw != nil {
			inventory.Write(entry.raw)
			continue
		}
		if err := writeV46Item(&inventory, entry); err != nil {
			return nil, err
		}
	}
	player := make([]byte, 0, len(p.player)+inventory.Len())
	player = append(player, p.player[:p.inventoryStart]...)
	player = append(player, inventory.Bytes()...)
	player = append(player, p.player[p.inventoryEnd:]...)
	payload := make([]byte, 0, len(p.payload)+inventory.Len())
	payload = append(payload, p.payload[:p.playerLengthOffset]...)
	payload = binary.LittleEndian.AppendUint32(payload, uint32(len(player)))
	payload = append(payload, player...)
	payload = append(payload, p.payload[p.playerEnd:]...)
	output := binary.LittleEndian.AppendUint32(nil, uint32(len(payload)))
	output = append(output, payload...)
	output = binary.LittleEndian.AppendUint32(output, 64)
	sum := sha512.Sum512(payload)
	output = append(output, sum[:]...)
	return output, nil
}

func runV46(req request, raw []byte) (response, error) {
	profile, err := parseV46(raw)
	if err != nil {
		return response{}, err
	}
	result := response{Name: profile.name}
	changed := false
	switch req.Action {
	case "inspect", "catalog":
		result.Catalog = items.Catalog().List()
	case "give":
		if req.Prefab == "" || len(req.Prefab) > 128 || strings.TrimSpace(req.Prefab) != req.Prefab || req.Quantity < 1 || req.Quantity > 1000 || req.Quality < 1 || req.Quality > 100 {
			return response{}, errors.New("invalid item, quantity, or quality")
		}
		metadata, known := items.Catalog().Lookup(req.Prefab)
		if known && (!metadata.InventoryValid || req.Quality > metadata.MaxQuality) {
			return response{}, errors.New("item is not valid at that quality")
		}
		maxStack, durability := int32(1), float32(100)
		if known {
			maxStack, durability = metadata.MaxStack, metadata.Durability(req.Quality)
		} else if req.MaxStack >= 1 && req.MaxStack <= 1000 {
			maxStack = req.MaxStack
		}
		if maxStack < 1 {
			maxStack = 1
		}
		for result.Given < req.Quantity {
			x, y, found := int32(0), int32(0), false
			for slot := int32(0); slot < 32 && !found; slot++ {
				x, y = slot%8, slot/8
				occupied := false
				for _, entry := range profile.entries {
					if entry.value.GridX == x && entry.value.GridY == y {
						occupied = true
						break
					}
				}
				if !occupied {
					found = true
				}
			}
			if !found {
				break
			}
			count := req.Quantity - result.Given
			if count > maxStack {
				count = maxStack
			}
			profile.entries = append(profile.entries, v46Item{value: valheim.Item{Name: req.Prefab, Stack: count, Quality: req.Quality, Durability: durability, GridX: x, GridY: y, PickedUp: true}, hash: stableHash(req.Prefab)})
			result.Given += count
		}
		if result.Given == 0 {
			return response{}, errors.New("inventory is full")
		}
		changed = true
	case "replace", "remove":
		if req.X < 0 || req.X > 7 || req.Y < 0 || req.Y > 3 {
			return response{}, errors.New("invalid inventory slot")
		}
		index := -1
		for i, entry := range profile.entries {
			if entry.value.GridX == req.X && entry.value.GridY == req.Y {
				index = i
				break
			}
		}
		if index < 0 {
			return response{}, errors.New("inventory slot is empty")
		}
		if req.Action == "remove" {
			profile.entries = append(profile.entries[:index], profile.entries[index+1:]...)
		} else {
			old := &profile.entries[index]
			if req.Item == nil || req.Item.Name != old.value.Name {
				return response{}, errors.New("item changed; refresh inventory")
			}
			if req.Item.Stack < 1 || req.Item.Stack > 1000 || req.Item.Quality < 1 || req.Item.Quality > 100 || req.Item.Durability < 0 {
				return response{}, errors.New("invalid item values")
			}
			metadata, known := items.Catalog().Lookup(old.value.Name)
			if known && (req.Item.Stack > metadata.MaxStack || req.Item.Quality > metadata.MaxQuality) {
				return response{}, errors.New("stack or quality exceeds item maximum")
			}
			old.value.Stack, old.value.Quality, old.value.Durability, old.value.Equipped, old.value.Variant = req.Item.Stack, req.Item.Quality, req.Item.Durability, req.Item.Equipped, req.Item.Variant
			old.raw = nil
		}
		changed = true
	default:
		return response{}, fmt.Errorf("unsupported action %q", req.Action)
	}
	result.Items = make([]valheim.Item, len(profile.entries))
	for i, entry := range profile.entries {
		result.Items[i] = entry.value
	}
	if changed {
		output, err := profile.encode()
		if err != nil {
			return response{}, err
		}
		if len(output) > 2*1024*1024 {
			return response{}, errors.New("edited character exceeds size limit")
		}
		if _, err := parseV46(output); err != nil {
			return response{}, err
		}
		result.Profile = base64.StdEncoding.EncodeToString(output)
	}
	return result, nil
}
