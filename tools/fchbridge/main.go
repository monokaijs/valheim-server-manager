package main

import (
 "encoding/base64"
 "encoding/binary"
 "encoding/json"
 "errors"
 "fmt"
 "io"
 "os"
 "strings"

 fch "github.com/lanchelms/fch-decoder"
 "github.com/lanchelms/fch-decoder/valheim"
 "github.com/lanchelms/fch-decoder/valheim/items"
)

type request struct {
 Action string `json:"action"`
 Profile string `json:"profile"`
 Prefab string `json:"prefab"`
 Quantity int32 `json:"quantity"`
 Quality int32 `json:"quality"`
 MaxStack int32 `json:"maxStack"`
 X int32 `json:"x"`
 Y int32 `json:"y"`
 Item *valheim.Item `json:"item"`
}

type response struct {
 Name string `json:"name"`
 Items []valheim.Item `json:"items"`
 Catalog any `json:"catalog,omitempty"`
 Given int32 `json:"given,omitempty"`
 Profile string `json:"profile,omitempty"`
}

func run(input io.Reader) (response, error) {
 var req request
 if err := json.NewDecoder(io.LimitReader(input, 3*1024*1024)).Decode(&req); err != nil { return response{}, err }
 raw, err := base64.StdEncoding.DecodeString(req.Profile)
 if err != nil || len(raw) > 2*1024*1024 { return response{}, errors.New("invalid character profile") }
 if len(raw) >= 8 && binary.LittleEndian.Uint32(raw[4:8]) == 46 { return runV46(req, raw) }
 if len(raw) >= 8 && binary.LittleEndian.Uint32(raw[4:8]) > 43 { return response{}, fmt.Errorf("unsupported character version %d (supported: 43 and 46)", binary.LittleEndian.Uint32(raw[4:8])) }
 character, err := fch.DecodeBytes(raw)
 if err != nil { return response{}, err }
 if err = character.ValidateEditable(); err != nil { return response{}, err }
 result := response{Name: character.Player.Name, Items: character.Player.Inventory}
 changed := false
 switch req.Action {
 case "inspect": result.Catalog = items.Catalog().List()
 case "catalog": result.Catalog = items.Catalog().List()
 case "give":
  if req.Prefab == "" || len(req.Prefab) > 128 || strings.TrimSpace(req.Prefab) != req.Prefab || req.Quantity < 1 || req.Quantity > 1000 || req.Quality < 1 || req.Quality > 100 { return response{}, errors.New("invalid item, quantity, or quality") }
  metadata, known := items.Catalog().Lookup(req.Prefab)
  if known && (!metadata.InventoryValid || req.Quality > metadata.MaxQuality) { return response{}, errors.New("item is not valid at that quality") }
  maxStack := int32(1)
  durability := float32(100)
  if known { maxStack = metadata.MaxStack; durability = metadata.Durability(req.Quality) }
  if !known && req.MaxStack >= 1 && req.MaxStack <= 1000 { maxStack = req.MaxStack }
  if maxStack < 1 { maxStack = 1 }
  for result.Given < req.Quantity {
   count := req.Quantity - result.Given
   if count > maxStack { count = maxStack }
   item := valheim.Item{Name: req.Prefab, Stack: count, Quality: req.Quality, Durability: durability, PickedUp: true}
   if err := character.PlaceInventoryItem(item); err != nil { break }
   result.Given += count
  }
  if result.Given == 0 { return response{}, errors.New("inventory is full") }
  changed = true
 case "replace":
  if req.Item == nil || req.X < 0 || req.X > 7 || req.Y < 0 || req.Y > 3 { return response{}, errors.New("invalid inventory slot") }
  old, ok := character.InventorySlot(req.X, req.Y)
  if !ok { return response{}, errors.New("inventory slot is empty") }
  if req.Item.Name != old.Name { return response{}, errors.New("item changed; refresh inventory") }
  if req.Item.Stack < 1 || req.Item.Stack > 1000 || req.Item.Quality < 1 || req.Item.Quality > 100 || req.Item.Durability < 0 { return response{}, errors.New("invalid item values") }
  metadata, known := items.Catalog().Lookup(req.Item.Name)
  if known && (req.Item.Stack > metadata.MaxStack || req.Item.Quality > metadata.MaxQuality) { return response{}, errors.New("stack or quality exceeds item maximum") }
  old.Stack, old.Quality, old.Durability, old.Equipped, old.Variant = req.Item.Stack, req.Item.Quality, req.Item.Durability, req.Item.Equipped, req.Item.Variant
  changed = true
 case "remove":
  if req.X < 0 || req.X > 7 || req.Y < 0 || req.Y > 3 { return response{}, errors.New("invalid inventory slot") }
  for index, item := range character.Player.Inventory {
   if item.GridX == req.X && item.GridY == req.Y {
    character.Player.Inventory = append(character.Player.Inventory[:index], character.Player.Inventory[index+1:]...)
    changed = true
    break
   }
  }
  if !changed { return response{}, errors.New("inventory slot is empty") }
 default: return response{}, fmt.Errorf("unsupported action %q", req.Action)
 }
 if changed {
  encoded, err := fch.EncodeBytes(character)
  if err != nil { return response{}, err }
  if len(encoded) > 2*1024*1024 { return response{}, errors.New("edited character exceeds size limit") }
  result.Profile = base64.StdEncoding.EncodeToString(encoded)
  result.Items = character.Player.Inventory
 }
 return result, nil
}

func main() {
 result, err := run(os.Stdin)
 if err != nil { fmt.Fprintln(os.Stderr, err); os.Exit(1) }
 if err := json.NewEncoder(os.Stdout).Encode(result); err != nil { fmt.Fprintln(os.Stderr, err); os.Exit(1) }
}
