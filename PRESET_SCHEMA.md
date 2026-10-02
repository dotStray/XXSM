# The Game Pack format

A **Game Pack** tells XXSM everything about one game: its characters and outfits, their
portraits, and the hashes that tell one character's mod from another's. XXSM itself knows
nothing about any game, so the pack is what makes it useful.

Most people never need this file. **Pack Studio**, inside the app, makes packs without
anyone touching a file by hand. This page is for anyone who wants to build packs with their
own scripts instead, the way [xxsm-presets](https://github.com/dotStray/xxsm-presets) does.

The current format is version 1 (`packSchemaVersion: 1`).

---

## 1. How the format changes

- **Fields are only ever added.** A new optional field can appear at any time. XXSM skips
  fields it does not know, so an older XXSM still reads a newer pack.
- **Removing a field, or changing what one means or holds, needs a new version number.**
  XXSM lists a pack whose version it does not support as "requires a newer XXSM" and does
  not load it.
- **Ids are permanent.** Every id (a `gameId`, an `internalName`, an attribute or value id)
  uses only letters, digits, `_` and `-`, and capitals do not matter: `Ganyu` and `ganyu`
  are the same id. Changing a display name is always fine. Changing an `internalName`
  breaks every user's filing for that character, so keep the old one in `aliases` if you
  ever have to.

---

## 2. What is in a pack

A pack is a zip file:

```
manifest.json              what the pack is: game, version, where its data came from (2.1)
game.json                  the game itself, and the filters it offers (2.2)
variants.json              every character and outfit (2.3)
hashes.json                every character's hashes (2.4)
images/                    portraits and icons (2.5)
LICENSE or ATTRIBUTION.md  optional: credits for the data
```

The examples below are taken from the Genshin Impact pack that xxsm-presets publishes.

### 2.1 `manifest.json`: what the pack is

```json
{
  "packSchemaVersion": 1,
  "gameId": "genshin",
  "packVersion": "2026.10.02",
  "generatedAt": "2026-10-02T00:00:00Z",
  "builder": "packbuilder 0.1.0",
  "authoredBy": "official",
  "minAppVersion": "0.1.0",
  "counts": { "variants": 156, "skins": 30, "images": 156 },
  "sources": [
    { "kind": "hashes", "url": "https://github.com/SilentNightSound/GI-Model-Importer-Assets",
      "commit": "2039d16d4b64696098ba53cd69888ce967397be9", "license": "GPL-3.0" },
    { "kind": "images", "url": "https://cdn.gachabase.net/gi/assets/",
      "license": "Game art © COGNOSPHERE / HoYoverse" }
  ]
}
```

| Field | What it is |
|---|---|
| `packSchemaVersion` | The format version, `1`. |
| `gameId` | The game's id. Must match `game.json`. |
| `packVersion` | This pack's version. A date such as `2026.10.02` works well: newer versions sort later. |
| `generatedAt` | When it was built. |
| `builder` | What built it, for example `packbuilder 0.1.0`, or `XXSM Pack Studio` for a Studio export. |
| `authoredBy` | `official` or `user`. Shown to people, never used to decide anything. |
| `minAppVersion` | The oldest XXSM that can use this pack. |
| `counts` | How many characters, outfits and pictures it holds. Shown to people only. |
| `sources` | Where the data came from, with a link and licence for each. |

### 2.2 `game.json`: the game

```json
{
  "gameId": "genshin",
  "displayName": "Genshin Impact",
  "shortName": "GI",
  "importer": "GIMI",
  "icon": "images/_game.webp",
  "disabledPrefix": "DISABLED_",
  "attributes": {
    "element": { "displayName": "Element",
      "values": [ { "id": "anemo", "displayName": "Anemo" },
                  { "id": "cryo",  "displayName": "Cryo" } ] },
    "weaponClass": { "displayName": "Weapon",
      "values": [ { "id": "bow", "displayName": "Bow" },
                  { "id": "sword", "displayName": "Sword" } ] },
    "rarity": { "displayName": "Rarity", "kind": "number" }
  }
}
```

| Field | What it is |
|---|---|
| `gameId`, `displayName`, `shortName` | The game's id, its full name, and a short one. |
| `importer` | The game's folder name in XXMI (`GIMI`, `SRMI`, `ZZMI`, …). For information only. |
| `icon` | The game's icon, inside `images/`. |
| `disabledPrefix` | What a switched-off mod's folder starts with. 3DMigoto uses `DISABLED_`. |
| `attributes` | The filters shown above the character grid (below). |

**Attributes are whatever the game needs.** Each one becomes a row of filter chips on the
character grid. A game without elements simply has no `element`; a game with something no
other game has just adds it, and XXSM shows it without any change to the app. Each
attribute has a `displayName` and either a list of `values` (each with an `id`, a
`displayName` and an optional `icon`), or `"kind": "number"` for something like rarity.

**Weapons, NPCs and other things mods can change go in the same list as characters.** There
is no special category for them. To let people filter them, add an attribute of your own,
for example:

```json
"kind": { "displayName": "Kind",
  "values": [ { "id": "weapon", "displayName": "Weapons" },
              { "id": "npc", "displayName": "NPCs" } ] }
```

Anything without it counts as a character.

### 2.3 `variants.json`: every character and outfit

A **variant** is one thing a mod can be made for: a character, or one of their outfits.
They are all in one list:

```json
[
  {
    "internalName": "Ganyu",
    "displayName": "Ganyu",
    "baseCharacterId": null,
    "isDefaultVariant": true,
    "modFilesName": "Ganyu",
    "image": "images/Ganyu.webp",
    "attributes": { "element": "cryo", "weaponClass": "bow", "rarity": 5 }
  },
  {
    "internalName": "GanyuTwilight",
    "displayName": "Twilight Blossom",
    "baseCharacterId": "Ganyu",
    "isDefaultVariant": false,
    "modFilesName": "GanyuTwilight",
    "image": "images/GanyuTwilight.webp",
    "attributes": { "element": "cryo", "weaponClass": "bow", "rarity": 5 }
  },
  {
    "internalName": "Alyosha",
    "displayName": "Alyosha",
    "baseCharacterId": null,
    "isDefaultVariant": true,
    "modFilesName": "Alyosha",
    "image": "images/Alyosha.webp",
    "attributes": { "element": "electro", "weaponClass": "polearm", "rarity": 4 },
    "hashesPending": true
  }
]
```

| Field | What it is |
|---|---|
| `internalName` | **Required.** The character's permanent id. |
| `displayName` | **Required.** The name people see. |
| `baseCharacterId` | For an outfit, the `internalName` of the character it belongs to. `null` for a character. |
| `isDefaultVariant` | `true` for the one variant in each family that a mod goes to when XXSM cannot tell which outfit it is for. Normally the character itself. |
| `modFilesName` | The character's folder name in the Mods folder. XXSM also uses it to guess from file names. Defaults to `internalName`. |
| `image` | The portrait, inside `images/`, or a full `https://` address. |
| `attributes` | This variant's value for each attribute in `game.json`. |
| `aliases` | Other names XXSM should recognise in a mod's file names, for example `["甘雨"]`. |
| `hashesPending` | `true` when the character exists but no hashes have been published for them yet (below). |
| `hidden` | `true` keeps the variant off the grid. |
| `releaseDate` | When the character came out, for example `"2021-01-12"`. Kept, but not shown at the moment. |
| `notes` | Free text, kept with the character. |

A few rules:

- **Outfits are not nested.** A character and their outfits are separate entries, joined
  by `baseCharacterId`. Each one gets its own folder in the Mods folder, just as the list
  suggests.
- **One per family is the default.** Exactly one variant of each character (the character
  or one of their outfits) has `isDefaultVariant: true`.
- **An outfit must point at a character in the same file.** If it does not, XXSM warns and
  treats the outfit as a character of its own.

**A character with no hashes yet is normal.** When a character is released, the character
lists know about them long before the hash repositories catch up. Put them in the pack
anyway, with no hash entries and `hashesPending: true`. XXSM shows them like everyone else,
files mods for them by name and by file name, lets people file mods by hand, and offers to
learn their hashes from a mod. The flag is how the app knows to say "no hashes yet" rather
than leave people wondering why sorting misses them; a character with no hash entries is
handled the same way even without it.

### 2.4 `hashes.json`: the hashes

One flat list, one entry per hash:

```json
{
  "ignoredHashes": ["653c63ba4a73ca8b"],
  "entries": [
    { "variant": "Ganyu", "component": "", "kind": "ib",          "hash": "1575ec63" },
    { "variant": "Ganyu", "component": "", "kind": "position_vb", "hash": "a5169f1d" },
    { "variant": "Ganyu", "component": "", "kind": "draw_vb",     "hash": "fbf98643" },
    { "variant": "Ganyu", "component": "", "kind": "texture",
      "hash": "6d78ac96", "textureKind": "Diffuse", "slot": 0 }
  ]
}
```

| Field | What it is |
|---|---|
| `variant` | The `internalName` the hash belongs to. |
| `component` | Which part of the model it belongs to, such as `Body` or `Face`. Can be `""`. |
| `kind` | `ib`, `position_vb`, `blend_vb`, `texcoord_vb`, `draw_vb`, `root_vs`, `texture`, or `unknown`. |
| `hash` | The hash, lowercase hex. |
| `textureKind`, `slot` | For a texture: its kind (`Diffuse`, …) and which object it belongs to. |

- **`ignoredHashes`** lists hashes XXSM must never use to decide who a mod is for. Shaders
  shared by many characters belong here: they appear in everyone's mods and prove nothing.
  XXSM also ignores, by itself, any hash that turns up in too many characters.
- **`unknown`** is for a hash someone added by hand without saying which kind it is. It
  counts as much as a `draw_vb`.
- Never write an empty hash.

### 2.5 `images/`: portraits and icons

- `<internalName>.webp` (or `.png`, `.jpg`) for each variant's portrait, plus
  `_game.webp` for the game's icon and any icons your attribute values name.
- WebP, PNG and JPEG all work, as long as the extension matches the format.
- Around 512 pixels across and under 200 KB is plenty: everyone who installs the pack
  downloads every picture. Pack Studio warns about anything larger.
- A missing portrait is fine. XXSM shows the character's initials instead.

---

## 3. The registry: the list of packs

XXSM finds packs through a **registry**, an `index.json` file at a web address (or in a
folder). The default is the one published from
[xxsm-presets](https://github.com/dotStray/xxsm-presets). People can add their own in
Settings; when two list the same game, the later one wins.

```json
{
  "schemaVersion": 1,
  "updatedAt": "2026-10-02T00:00:00Z",
  "packs": [
    {
      "gameId": "genshin",
      "displayName": "Genshin Impact",
      "shortName": "GI",
      "importer": "GIMI",
      "versions": [
        {
          "packVersion": "2026.10.02",
          "packSchemaVersion": 1,
          "minAppVersion": "0.1.0",
          "url": "https://github.com/dotStray/xxsm-presets/releases/download/2026.10.02/genshin-2026.10.02.zip",
          "sha256": "efad32ada01b31a94b0b6df0c5a832f98ff82076a7bba7d82f43a7ee7dd16d14",
          "sizeBytes": 3210012,
          "changelog": "126 characters and 30 skins, 23 still waiting for hashes."
        }
      ]
    }
  ]
}
```

- **Every pack needs its `sha256` checksum.** XXSM checks the download against it and
  refuses a pack that does not match.
- **List versions newest first.** XXSM takes the newest one whose format it supports and
  whose `minAppVersion` it meets.
- **Use `https://` addresses.**
- There are no signatures, only checksums, so XXSM shows beside every pack which registry
  it comes from. A pack is only ever data; nothing in it runs.

---

## 4. Pack Studio makes the same packs

Pack Studio and outside builders write exactly the same format; the only difference is
what `builder` and `authoredBy` say in the manifest. Everything on this page applies to
both. That includes writing the same pack the same way every time (keys and lists in a
fixed order, fixed dates inside the zip), so building an unchanged pack twice gives the
same file and the difference between two versions is easy to read.

It works the other way too: Pack Studio can open any valid pack, including one from
xxsm-presets, change it and export it again.
