using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using UnityEngine.Timeline;

namespace BovineLabs.Timeline.Editor
{
    public static class TimelineGroupTool
    {
        const string SplitPath = "CONTEXT/PlayableDirector/Split Groups";
        const string JoinPath = "CONTEXT/PlayableDirector/Join Timelines";

        [MenuItem(SplitPath)]
        static void SplitFromInspector(MenuCommand cmd)
        {
            if (cmd.context is PlayableDirector d) Expand(d, false);
        }

        [MenuItem(SplitPath, true)]
        static bool CanSplit(MenuCommand cmd)
        {
            return cmd.context is PlayableDirector d && d.playableAsset is TimelineAsset tl
                && tl.GetRootTracks().Any(t => t is GroupTrack);
        }

        [MenuItem(JoinPath)]
        static void JoinFromInspector(MenuCommand cmd)
        {
            var dirs = Selection.gameObjects
               .Select(go => go.GetComponent<PlayableDirector>())
               .Where(d => d != null && d.playableAsset is TimelineAsset)
               .Distinct()
               .ToArray();
            if (dirs.Length >= 2) Collapse(dirs);
        }

        [MenuItem(JoinPath, true)]
        static bool CanJoin(MenuCommand cmd)
        {
            return Selection.gameObjects.Length >= 2
                && Selection.gameObjects.All(go => go.GetComponent<PlayableDirector>()?.playableAsset is TimelineAsset);
        }

        public static void Expand(PlayableDirector source, bool deleteSourceGroups = false)
        {
            var sourceAsset = source.playableAsset as TimelineAsset;
            if (sourceAsset == null) return;

            // The director (and therefore the objects we create) must live in a real, saved scene. Creating siblings in
            // the wrong scene is what makes split objects "pop open" a SubScene or land in the wrong open scene.
            var scene = source.gameObject.scene;
            if (!EnsureSceneSaved(scene)) return;

            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();

            var directory = GetDirectory(sourceAsset);
            var parent = source.transform.parent;
            var siblingIndex = source.transform.GetSiblingIndex();
            var groups = sourceAsset.GetRootTracks().Where(t => t is GroupTrack).Cast<GroupTrack>().ToList();

            try
            {
                AssetDatabase.StartAssetEditing();
                for (int i = 0; i < groups.Count; i++)
                {
                    var group = groups[i];
                    EditorUtility.DisplayProgressBar("Splitting Timeline", group.name, i / (float)groups.Count);

                    // Skip empty groups instead of emitting an orphan, track-less .playable.
                    if (!group.GetChildTracks().Any())
                    {
                        Debug.LogWarning($"Group Tool: skipped empty group '{group.name}' (no child tracks).");
                        continue;
                    }

                    // Persist the destination asset FIRST so the clip sub-assets we add below are actually serialized into
                    // the .playable file. Without this they are loose in-memory objects that vanish on the next domain
                    // reload (e.g. entering Play) and the new timeline silently becomes empty.
                    var newAsset = ScriptableObject.CreateInstance<TimelineAsset>();
                    newAsset.name = $"{sourceAsset.name}_{group.name}";
                    var path = AssetDatabase.GenerateUniqueAssetPath($"{directory}/{newAsset.name}.playable");
                    AssetDatabase.CreateAsset(newAsset, path);

                    var map = new Dictionary<TrackAsset, TrackAsset>();
                    foreach (var child in group.GetChildTracks())
                        CloneTrackRecursive(child, newAsset, null, map);
                    EditorUtility.SetDirty(newAsset);

                    var go = new GameObject(group.name);
                    PlaceInScene(go, parent, scene, ++siblingIndex);
                    Undo.RegisterCreatedObjectUndo(go, "Split Groups");

                    var dir = go.AddComponent<PlayableDirector>();
                    Undo.RegisterCreatedObjectUndo(dir, "Split Groups");
                    dir.playableAsset = newAsset;
                    CopyDirectorSettings(source, dir);
                    TransferBindings(source, dir, map);
                }

                if (deleteSourceGroups)
                {
                    foreach (var g in groups)
                        sourceAsset.DeleteTrack(g);
                    EditorUtility.SetDirty(sourceAsset);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                EditorUtility.ClearProgressBar();
                AssetDatabase.SaveAssets();
                EditorSceneManager.MarkSceneDirty(scene);
                Undo.CollapseUndoOperations(undoGroup);
            }
        }

        public static void Collapse(params PlayableDirector[] sources)
        {
            sources = sources.Where(s => s != null && s.playableAsset is TimelineAsset).Distinct().ToArray();
            if (sources.Length < 2) return;
            if (!EditorUtility.DisplayDialog("Join Timelines",
                $"Merge {sources.Length} timelines into one? Sources will be moved to Assets/_TimelineBackups (not deleted).",
                "Join", "Cancel")) return;

            var first = sources[0];
            var scene = first.gameObject.scene;
            if (!EnsureSceneSaved(scene)) return;

            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();

            var directory = GetDirectory(first.playableAsset);
            var merged = ScriptableObject.CreateInstance<TimelineAsset>();
            merged.name = $"{first.gameObject.name}_Merged";

            // bindingMap is keyed by the CLONE track (the one the merged director actually owns), never the source track.
            var bindingMap = new Dictionary<TrackAsset, Object>();

            try
            {
                AssetDatabase.StartAssetEditing();

                // Persist before cloning so clip sub-assets stick (see Expand for the why).
                var path = AssetDatabase.GenerateUniqueAssetPath($"{directory}/{merged.name}.playable");
                AssetDatabase.CreateAsset(merged, path);

                for (int i = 0; i < sources.Length; i++)
                {
                    var src = sources[i];
                    EditorUtility.DisplayProgressBar("Joining Timelines", src.gameObject.name, i / (float)sources.Length);

                    var srcAsset = (TimelineAsset)src.playableAsset;
                    var group = merged.CreateTrack<GroupTrack>(null, srcAsset.name);

                    var map = new Dictionary<TrackAsset, TrackAsset>();
                    foreach (var rootTrack in srcAsset.GetRootTracks())
                        CloneTrackRecursive(rootTrack, merged, group, map);

                    // Map every source track (root + nested) to its clone, then key the binding by the clone.
                    foreach (var kv in map)
                    {
                        var binding = src.GetGenericBinding(kv.Key);
                        if (binding != null) bindingMap[kv.Value] = binding;
                    }
                }

                EditorUtility.SetDirty(merged);

                var parent = first.transform.parent;
                var siblingIndex = first.transform.GetSiblingIndex();
                var go = new GameObject(merged.name);
                PlaceInScene(go, parent, scene, siblingIndex);
                Undo.RegisterCreatedObjectUndo(go, "Join Timelines");

                var dir = go.AddComponent<PlayableDirector>();
                Undo.RegisterCreatedObjectUndo(dir, "Join Timelines");
                dir.playableAsset = merged;
                CopyDirectorSettings(first, dir);

                foreach (var kv in bindingMap)
                    dir.SetGenericBinding(kv.Key, kv.Value);

                BackupAndDisableSources(sources);
                Selection.activeGameObject = go;
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                EditorUtility.ClearProgressBar();
                AssetDatabase.SaveAssets();
                EditorSceneManager.MarkSceneDirty(scene);
                Undo.CollapseUndoOperations(undoGroup);
            }
        }

        static TrackAsset CloneTrackRecursive(TrackAsset source, TimelineAsset dest, TrackAsset parent,
            Dictionary<TrackAsset, TrackAsset> map)
        {
            var clone = dest.CreateTrack(source.GetType(), parent, source.name);
            map[source] = clone;

            clone.muted = source.muted;
            clone.locked = source.locked;

            // Do NOT CopySerialized an AnimationTrack: it bulk-copies internal fields (m_Clips, m_Markers, parent and
            // owning-asset references), which corrupts the freshly created clone. Copy only the user-facing fields.
            if (source is AnimationTrack animSrc && clone is AnimationTrack animDst)
                CopyAnimationTrackFields(animSrc, animDst);

            foreach (var clip in source.GetClips())
            {
                var newClip = clone.CreateDefaultClip();
                if (clip.asset != null)
                {
                    var defaultAsset = newClip.asset;
                    var newPlayableAsset = Object.Instantiate(clip.asset);
                    newPlayableAsset.name = clip.asset.name;
                    newClip.asset = newPlayableAsset;

                    // Register the real clip asset as a sub-asset of the persisted timeline and discard the throwaway
                    // default asset CreateDefaultClip produced, so it is not left orphaned in the .playable file.
                    if (AssetDatabase.Contains(dest))
                    {
                        AssetDatabase.AddObjectToAsset(newPlayableAsset, dest);
                        if (defaultAsset != null) Object.DestroyImmediate(defaultAsset, true);
                    }
                }

                newClip.start = clip.start;
                newClip.duration = clip.duration;
                newClip.displayName = clip.displayName;
                newClip.clipIn = clip.clipIn;
                newClip.timeScale = clip.timeScale;
                newClip.blendInDuration = clip.blendInDuration;
                newClip.blendOutDuration = clip.blendOutDuration;
                newClip.easeInDuration = clip.easeInDuration;
                newClip.easeOutDuration = clip.easeOutDuration;
                newClip.mixInCurve = clip.mixInCurve;
                newClip.mixOutCurve = clip.mixOutCurve;
            }

            foreach (var marker in source.GetMarkers())
            {
                if (marker is ScriptableObject so)
                {
                    var newMarker = clone.CreateMarker(so.GetType(), marker.time);
                    if (newMarker is ScriptableObject newSo)
                        EditorUtility.CopySerialized(so, newSo);
                }
            }

            foreach (var child in source.GetChildTracks())
                CloneTrackRecursive(child, dest, clone, map);

            return clone;
        }

        static void CopyAnimationTrackFields(AnimationTrack src, AnimationTrack dst)
        {
            dst.trackOffset = src.trackOffset;
            dst.position = src.position;
            dst.rotation = src.rotation;
            dst.applyAvatarMask = src.applyAvatarMask;
            dst.avatarMask = src.avatarMask;
        }

        // Binding transfer for both paths is driven entirely by the source->clone map built during cloning. This covers
        // nested groups and tracks that share a name (FirstOrDefault name matching dropped both silently).
        static void TransferBindings(PlayableDirector src, PlayableDirector dst, Dictionary<TrackAsset, TrackAsset> map)
        {
            foreach (var kv in map)
            {
                var binding = src.GetGenericBinding(kv.Key);
                if (binding != null) dst.SetGenericBinding(kv.Value, binding);
            }
        }

        static void CopyDirectorSettings(PlayableDirector src, PlayableDirector dst)
        {
            dst.timeUpdateMode = src.timeUpdateMode;
            dst.extrapolationMode = src.extrapolationMode;
            dst.playOnAwake = false;
            dst.initialTime = src.initialTime;
            dst.time = src.time;
        }

        static void BackupAndDisableSources(PlayableDirector[] sources)
        {
            var backupDir = "Assets/_TimelineBackups";
            if (!AssetDatabase.IsValidFolder(backupDir))
                AssetDatabase.CreateFolder("Assets", "_TimelineBackups");

            foreach (var src in sources)
            {
                var path = AssetDatabase.GetAssetPath(src.playableAsset);
                if (!string.IsNullOrEmpty(path))
                {
                    var fileName = Path.GetFileName(path);
                    var dest = AssetDatabase.GenerateUniqueAssetPath($"{backupDir}/{fileName}");
                    var error = AssetDatabase.MoveAsset(path, dest);
                    if (!string.IsNullOrEmpty(error))
                        Debug.LogWarning($"Group Tool: failed to back up '{path}': {error}");
                }

                Undo.RegisterCompleteObjectUndo(src.gameObject, "Backup");
                src.gameObject.SetActive(false);
            }
        }

        // Parents the new object correctly AND guarantees it ends up in the source director's scene. SetParent inherits
        // the parent's scene; a root-level object would otherwise default to the active scene, which is exactly what
        // breaks when the director lives in a SubScene or a non-active open scene.
        static void PlaceInScene(GameObject go, Transform parent, Scene scene, int siblingIndex)
        {
            if (parent != null)
            {
                go.transform.SetParent(parent, false);
            }
            else if (scene.IsValid() && go.scene != scene)
            {
                SceneManager.MoveGameObjectToScene(go, scene);
            }

            go.transform.SetSiblingIndex(siblingIndex);
        }

        static bool EnsureSceneSaved(Scene scene)
        {
            if (scene.IsValid() && string.IsNullOrEmpty(scene.path))
            {
                return EditorUtility.DisplayDialog("Unsaved Scene",
                    "The scene that owns this director has never been saved. Objects created by this tool live only in " +
                    "that scene and will be lost if the editor closes before you save it.\n\nContinue anyway?",
                    "Continue", "Cancel");
            }

            return true;
        }

        static string GetDirectory(Object asset)
        {
            var path = AssetDatabase.GetAssetPath(asset);
            return string.IsNullOrEmpty(path) ? "Assets" : Path.GetDirectoryName(path).Replace("\\", "/");
        }
    }
}
