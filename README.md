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
5. Source GameObjects and their timeline assets are deleted from disk after the merge succeeds.
6. The merged `GameObject` replaces the first source in the hierarchy.

## What Gets Preserved

- Track types (ActivationTrack, AnimationTrack, etc.)
- Clip timing (start, duration, clip-in, time scale)
- Clip assets (PlayableAssets are cloned into the target timeline)
- Director bindings (GenericBindings transfer to new tracks)
- Hierarchy position (sibling index and parent)

## Requirements

- Unity 2022.3+
- Timeline package 1.7.0+

## License

MIT
