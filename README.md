# BovineLabs Timeline Group Tool

Split and join Timeline GroupTracks directly from the PlayableDirector inspector context menu.

## Install

Add to `Packages/manifest.json`:

```json
"com.bovinelabs.timeline.group-tool": "https://github.com/IAFahim/com.bovinelabs.timeline.group-tool.git"
```

## Usage

### Split (1 → Many)

1. Select a GameObject with a `PlayableDirector` whose `TimelineAsset` contains root `GroupTrack`s.
2. Right-click the **PlayableDirector** component header (three-dot menu) in the Inspector.
3. Click **Split Groups**.
4. Each root `GroupTrack` becomes its own `TimelineAsset` + `GameObject` with raw child tracks (no group wrapper).
5. The original timeline is left untouched. New GameObjects are placed as siblings right after it.

### Join (Many → 1)

1. Select **2 or more** GameObjects, each with a `PlayableDirector` and a `TimelineAsset`.
2. Right-click the **PlayableDirector** component header (three-dot menu) in the Inspector.
3. Click **Join Timelines**.
4. All selected timelines merge into a single `TimelineAsset` with `GroupTrack`s named after each source timeline.
5. Source GameObjects are **disabled**, and their `.playable` assets are **moved to `Assets/_TimelineBackups`** (never deleted). Delete that folder yourself once you're happy with the result.
6. The merged `GameObject` replaces the first source in the hierarchy.

## What Gets Preserved

- Track types (ActivationTrack, AnimationTrack, etc.), including nested `GroupTrack`s
- Clip timing (start, duration, clip-in, time scale, blends, eases, mix curves)
- Clip assets — `PlayableAsset`s are cloned **and registered as sub-assets**, so they survive entering Play and reopening the project
- Markers / signals (copied per marker)
- Director bindings (transferred via a source→clone track map, so duplicate names and nested-group bindings are kept)
- Hierarchy position (parent and sibling index)
- **Scene of origin** — new objects are created in the same scene/SubScene as the source director, not the active scene

## Notes & Limitations

- New scene objects are fully undoable (Ctrl+Z). The `.playable` asset files created/moved on disk are **not** removed by undo — clean them up manually if you undo.
- If the source director's scene has never been saved, the tool warns first (created objects live only in that scene).

## Requirements

- Unity 2022.3+
- Timeline package 1.7.0+

## License

MIT
