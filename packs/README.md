# Texture and audio replacement packs

A pack is a plain folder (or a .zip file) in this `packs` folder. You don't need to compile anything. The game reads
every pack at startup and swaps in your files as it draws, so restart the game after you add or change a pack.

## Replacing a texture

### 1. Dump the original textures

Start the game and open **Debug > Texture Inspector**. Tick "dump tiles", then play through the part of the game
that shows the texture you're after (a menu, a track, a car). Every texture the game draws is saved as a PNG next to
`nfs4.exe`, in:

```
dump\SLUS-00826\textures\
```

The Texture Inspector also lists what's currently on screen, which makes the right file easier to find.

### 2. Find the file you want to change

Dumped files are named `<index hash>_<clut hash>.png`, for example:

```
3f9a1c0d22e4b781_00c4a1e9f0b2d356.png
```

The first hash identifies the texture's pixels and the second its colour palette (CLUT). The same picture with a
different palette, such as another car colour, gets a file of its own. To replace a texture for every palette at
once, name your file with the first hash only: `3f9a1c0d22e4b781.png`.

### 3. Edit it

Any image editor works. The PNG can be bigger than the original (2x or 4x for HD) as long as the aspect ratio stays
the same, otherwise it gets stretched. Transparency comes from the PNG's alpha channel.

### 4. Make a pack

1. Copy the `.example-pack` folder and rename the copy, for example to `my-pack`. Folders whose name starts with a
   dot are ignored, which is why the example itself never loads.
2. In `my-pack\pack.json`, set your own `id` and `name`.
3. Put the edited PNG in `my-pack\textures\`, with exactly the same file name as the dump.

### 5. Restart the game

The log line `[assets] game=SLUS-00826 packs=1 ... tex=N` tells you how many textures were loaded. In the Texture
Inspector, tick "replaced" to list only the textures your pack replaced.

## Colour variants

Lots of dumps share the first hash and differ only in the second. Those are the same picture with different
palettes: car paint colours, lighting variants, a menu item drawn highlighted and normal.

To find the one the game is actually using:

1. Get it on screen, e.g. the car in the colour you picked.
2. In the Texture Inspector, tick "recent" (newest first), plus "pages" if you're looking for a page.
3. Click the hash next to the thumbnail to copy it to the clipboard, then search the dump folder for it.

Then decide how to replace it:

| File name | What it replaces |
|---|---|
| `<pixels>_<palette>.png` | only that colour variant |
| `<pixels>.png` | every variant, all with your image and your colours |
| `<pixels>.idx.png` | every variant, recoloured by the game with each palette |

The `.idx.png` option starts from the greyscale indexed dump that's written next to each tile dump. Each grey level
stands for one palette slot, so only use grey levels that are already in the image (no new colours, no smoothing),
and only upscale with nearest-neighbour. Pages don't get an `.idx.png`.

## Replacing a whole texture page

A page is the full 256x256 (4-bit) or 128x256 (8-bit) sheet that many small textures are cut from. Tick "dump pages"
in the Texture Inspector to save pages to `dump\SLUS-00826\pages\`.

Put an edited page in your pack's `textures\` folder under the same file name. There's no separate folder for pages.
You can upscale it as long as the aspect ratio stays the same (512x512 or 256x512, say).

A few rules apply to pages:

- A tile replacement always takes priority. The page is only used for textures that have no tile of their own.
- Only 4-bit and 8-bit (paletted) textures use pages. 16-bit textures can only be replaced as tiles.
- The page hash covers the whole page plus its palette. When the game loads different textures into the same page at
  different times (per track or per car, for instance), each combination counts as a different page, and your
  replacement only applies to the one you dumped. Replace tiles instead for textures that move around like that.
- Pages the game draws into (render targets) are never replaced.

## Pack folder layout

```
packs\
  my-pack\
    pack.json                              required, see below
    textures\<index hash>_<clut hash>.png  replaces that texture with that palette
    textures\<index hash>.png              replaces that texture with any palette
    textures\<page hash>_<clut hash>.png   a dumped page, same naming as tiles
    cluts\<clut hash>.clut.png             optional, replaces a whole colour palette
```

You can also zip a pack and drop the .zip in here as it is. Put `pack.json` at the top level of the zip.

## pack.json

```json
{
  "formatVersion": 1,
  "id": "my-pack",
  "name": "My Pack",
  "version": "1.0",
  "author": "you",
  "description": "what it changes",
  "priority": 0,
  "game": { "id": "SLUS-00826" }
}
```

| Field | Meaning |
|---|---|
| `id` | Required, and must be unique. |
| `priority` | Higher loads later. When two packs replace the same texture, the higher one wins. |
| `game.id` | The pack only loads for this disc. `SLUS-00826` is the US release of Need for Speed: High Stakes. |

pack.json can also declare texture rules and replacements for XA music and voice tracks. All the fields are listed in
`external\RecompOne\RecompOne.Runtime\Assets\PackManifest.cs`.
