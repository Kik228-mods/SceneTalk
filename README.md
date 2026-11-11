# SceneTalk

A MelonLoader mod for **My Dystopian Robot Girlfriend** (PC only, tested on 0.97.12).

Adds a **"Say..."** button to sex scenes (stream and non-stream). Pick a tone - *Praise, Tender, Lewd, Command, Rude* or *Ask how she feels* - and she answers based on:

- her personality module (a dedicated set for the "Crimson Wanderer" module, a default set for everything else),
- her current sympathy and lust,
- how you treated her in the previous scene (be rude and she'll bring it up next time).

All lines live in plain text files, easy to edit or translate. English and Russian are included.

Written with a lot of help from AI (Claude). Rough code, expect bugs, back up your saves.

## Install

1. Install MelonLoader for the game (same as for any external mod).
2. Download the latest zip from [Releases](../../releases).
3. Copy `SceneTalk.dll` to `<game folder>\Mods`.
4. Copy the `SceneTalk` folder to `<game folder>\UserData\`, so you get `<game folder>\UserData\SceneTalk\lines_en.txt`.
5. Start the game and enter any sex scene - the "Say..." button appears in the scene button list.

## Settings

`UserData\SceneTalk\config.txt`:

| line | meaning |
|---|---|
| `lang=en` | use `lines_en.txt` (English) |
| `lang=ru` | use `lines.txt` (Russian) |
| `guid=...` | optional, see "No text!" below |

## Troubleshooting

- **Replies show "No text!"** - the game shows mod text through an installed mod that matches the *game* language. SceneTalk picks one automatically (check `MelonLoader\Latest.log` for `using mod for reply text`). You need at least one game mod installed. If it still fails, add `guid=<guid>` to `config.txt`, where `<guid>` is the file name of an installed mod's .zip in the game's Mods folder (under `AppData\LocalLow\IncontinentCell\My Dystopian Robot Girlfriend\Mods`).
- **Nothing happens when you pick a tone** - send the lines containing `SceneTalk` from `MelonLoader\Latest.log` in an issue.
- Memory of the last scene is stored in `UserData\SceneTalk\memory.txt` (delete it to reset).

## Lines format

```
[player.praise]            what you say (one is picked at random)
[default.praise.high]      her reply: <module>.<tone>.<level>
[alaya.rude.low]           module: alaya (Crimson Wanderer) or default
```

Levels: `low`, `mid`, `high`, `hot` (high sympathy + high lust). One line = one variant, ` | ` inside a line = several lines in a row.

## Works with LivelyLin

If [LivelyLin](../../../LivelyLin) is installed too, how you talk in scenes changes her overall mood, and when she is upset she answers coldly even at max sympathy. Not required.

## Building from source

`source\` has the C# code, project file and `build.bat`. Put the `source` folder inside the game folder (the project references `..\MelonLoader\...`) and run `build.bat` (needs the .NET 6+ SDK). The DLL is copied to `..\Mods`.

## License

MIT - do whatever you want, credit appreciated.

---

**Русский:** мод добавляет в сексуальные сцены кнопку «Сказать…» с выбором тона, Лин отвечает по модулю личности, симпатии, возбуждению и памяти о прошлой сцене. Установка та же, для русских реплик напишите `lang=ru` в `UserData\SceneTalk\config.txt`.
