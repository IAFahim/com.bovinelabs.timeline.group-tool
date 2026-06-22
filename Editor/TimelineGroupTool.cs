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
        private const string SplitPath = "CONTEXT/PlayableDirector/Split Groups";
        private const string JoinPath = "CONTEXT/PlayableDirector/Join Timelines";

        [MenuItem(SplitPath)]
        private static void SplitFromInspector(MenuCommand cmd)
        {
            if (cmd.context is PlayableDirector d) Expand(d);
        }

        [MenuItem(SplitPath, true)]
        private static bool CanSplit(MenuCommand cmd)
        {
            return cmd.context is PlayableDirector d && d.playableAsset is TimelineAsset tl
                                                     && tl.GetRootTracks().Any(t => t is GroupTrack);
        }

        [MenuItem(JoinPath)]
        private static void JoinFromInspector(MenuCommand cmd)
        {
            var dirs = Selection.gameObjects
                .Select(go => go.GetComponent<PlayableDirector>())
                .Where(d => d != null && d.playableAsset is TimelineAsset)
                .Distinct()
                .ToArray();
            if (dirs.Length >= 2) Collapse(dirs);
        }

        [MenuItem(JoinPath, true)]
        private static bool CanJoin(MenuCommand cmd)
        {
            return Selection.gameObjects.Length >= 2
                   && Selection.gameObjects.All(go =>
                       go.GetComponent<PlayableDirector>()?.playableAsset is TimelineAsset);
        }

        public static void Expand(PlayableDirector source, bool deleteSourceGroups = false)
        {
            var sourceAsset = source.playableAsset as TimelineAsset;
            if (sourceAsset == null) return;

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
                for (var i = 0; i < groups.Count; i++)
                {
                    var group = groups[i];
                    EditorUtility.DisplayProgressBar("Splitting Timeline", group.name, i / (float)groups.Count);

                    if (!group.GetChildTracks().Any())
                    {
                        Debug.LogWarning($"Group Tool: skipped empty group '{group.name}' (no child tracks).");
                        continue;
                    }

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

            var bindingMap = new Dictionary<TrackAsset, Object>();

            try
            {
                var path = AssetDatabase.GenerateUniqueAssetPath($"{directory}/{merged.name}.playable");
                AssetDatabase.CreateAsset(merged, path);

                CloneSourcesIntoMerged(sources, merged, bindingMap);
                EditorUtility.SetDirty(merged);

                var dir = CreateMergedDirector(first, merged, scene, bindingMap);

                BackupAndDisableSources(sources);
                Selection.activeGameObject = dir.gameObject;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.SaveAssets();
                EditorSceneManager.MarkSceneDirty(scene);
                Undo.CollapseUndoOperations(undoGroup);
            }
        }

        private static void CloneSourcesIntoMerged(PlayableDirector[] sources, TimelineAsset merged,
            Dictionary<TrackAsset, Object> bindingMap)
        {
            for (var i = 0; i < sources.Length; i++)
            {
                var src = sources[i];
                EditorUtility.DisplayProgressBar("Joining Timelines", src.gameObject.name, i / (float)sources.Length);

                var srcAsset = (TimelineAsset)src.playableAsset;
                var group = merged.CreateTrack<GroupTrack>(null, srcAsset.name);

                var map = new Dictionary<TrackAsset, TrackAsset>();
                foreach (var rootTrack in srcAsset.GetRootTracks())
                    CloneTrackRecursive(rootTrack, merged, group, map);

                foreach (var kv in map)
                {
                    var binding = src.GetGenericBinding(kv.Key);
                    if (binding != null) bindingMap[kv.Value] = binding;
                }
            }
        }

        private static PlayableDirector CreateMergedDirector(PlayableDirector first, TimelineAsset merged, Scene scene,
            Dictionary<TrackAsset, Object> bindingMap)
        {
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

            return dir;
        }

        private static TrackAsset CloneTrackRecursive(TrackAsset source, TimelineAsset dest, TrackAsset parent,
            Dictionary<TrackAsset, TrackAsset> map)
        {
            var clone = dest.CreateTrack(source.GetType(), parent, source.name);
            map[source] = clone;

            clone.muted = source.muted;
            clone.locked = source.locked;

            if (source is AnimationTrack animSrc && clone is AnimationTrack animDst)
                CopyAnimationTrackFields(animSrc, animDst, dest);

            foreach (var clip in source.GetClips())
            {
                var newClip = clone.CreateDefaultClip();
                if (clip.asset != null)
                {
                    var defaultAsset = newClip.asset;
                    var newPlayableAsset = Object.Instantiate(clip.asset);
                    newPlayableAsset.name = clip.asset.name;
                    newClip.asset = newPlayableAsset;

                    if (newPlayableAsset is AnimationPlayableAsset animPlayable && animPlayable.clip != null)
                    {
                        var clonedCurve = CloneEmbeddedClip(animPlayable.clip, source.timelineAsset, dest);
                        if (clonedCurve != null) animPlayable.clip = clonedCurve;
                    }

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
                if (marker is ScriptableObject so)
                {
                    var newMarker = clone.CreateMarker(so.GetType(), marker.time);
                    if (newMarker is ScriptableObject newSo)
                        EditorUtility.CopySerialized(so, newSo);
                }

            foreach (var child in source.GetChildTracks())
                CloneTrackRecursive(child, dest, clone, map);

            return clone;
        }

        private static void CopyAnimationTrackFields(AnimationTrack src, AnimationTrack dst, TimelineAsset dest)
        {
            dst.trackOffset = src.trackOffset;
            dst.position = src.position;
            dst.rotation = src.rotation;
            dst.applyAvatarMask = src.applyAvatarMask;
            dst.avatarMask = src.avatarMask;

            if (src.inClipMode || src.infiniteClip == null)
                return;

            var clonedClip = CloneEmbeddedClip(src.infiniteClip, src.timelineAsset, dest);
            if (clonedClip == null)
                return;

            var so = new SerializedObject(dst);
            so.FindProperty("m_InfiniteClip").objectReferenceValue = clonedClip;
            so.FindProperty("m_InfiniteClipOffsetPosition").vector3Value = src.infiniteClipOffsetPosition;
            so.FindProperty("m_InfiniteClipOffsetEulerAngles").vector3Value = src.infiniteClipOffsetEulerAngles;
            so.FindProperty("m_InfiniteClipPreExtrapolation").enumValueIndex = (int)src.infiniteClipPreExtrapolation;
            so.FindProperty("m_InfiniteClipPostExtrapolation").enumValueIndex = (int)src.infiniteClipPostExtrapolation;

            var timeOffset = so.FindProperty("m_InfiniteClipTimeOffset");
            if (timeOffset != null)
                CopyByName(src, timeOffset, "m_InfiniteClipTimeOffset");
            var footIK = so.FindProperty("m_InfiniteClipApplyFootIK");
            if (footIK != null)
                CopyByName(src, footIK, "m_InfiniteClipApplyFootIK");

            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void CopyByName(AnimationTrack src, SerializedProperty dstProp, string fieldName)
        {
            var srcProp = new SerializedObject(src).FindProperty(fieldName);
            if (srcProp == null)
                return;

            switch (dstProp.propertyType)
            {
                case SerializedPropertyType.Float:
                    dstProp.doubleValue = srcProp.doubleValue;
                    break;
                case SerializedPropertyType.Boolean:
                    dstProp.boolValue = srcProp.boolValue;
                    break;
            }
        }

        private static AnimationClip CloneEmbeddedClip(AnimationClip clip, Object sourceAsset, TimelineAsset dest)
        {
            if (clip == null || sourceAsset == null || !AssetDatabase.Contains(dest))
                return null;

            if (AssetDatabase.GetAssetPath(clip) != AssetDatabase.GetAssetPath(sourceAsset))
                return null;

            var clone = Object.Instantiate(clip);
            clone.name = clip.name;
            AssetDatabase.AddObjectToAsset(clone, dest);
            return clone;
        }

        private static void TransferBindings(PlayableDirector src, PlayableDirector dst,
            Dictionary<TrackAsset, TrackAsset> map)
        {
            foreach (var kv in map)
            {
                var binding = src.GetGenericBinding(kv.Key);
                if (binding != null) dst.SetGenericBinding(kv.Value, binding);
            }
        }

        private static void CopyDirectorSettings(PlayableDirector src, PlayableDirector dst)
        {
            dst.timeUpdateMode = src.timeUpdateMode;
            dst.extrapolationMode = src.extrapolationMode;
            dst.playOnAwake = false;
            dst.initialTime = src.initialTime;
            dst.time = src.time;
        }

        private static void BackupAndDisableSources(PlayableDirector[] sources)
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

        private static void PlaceInScene(GameObject go, Transform parent, Scene scene, int siblingIndex)
        {
            if (parent != null)
                go.transform.SetParent(parent, false);
            else if (scene.IsValid() && go.scene != scene) SceneManager.MoveGameObjectToScene(go, scene);

            go.transform.SetSiblingIndex(siblingIndex);
        }

        private static bool EnsureSceneSaved(Scene scene)
        {
            if (scene.IsValid() && string.IsNullOrEmpty(scene.path))
                return EditorUtility.DisplayDialog("Unsaved Scene",
                    "The scene that owns this director has never been saved. Objects created by this tool live only in " +
                    "that scene and will be lost if the editor closes before you save it.\n\nContinue anyway?",
                    "Continue", "Cancel");

            return true;
        }

        private static string GetDirectory(Object asset)
        {
            var path = AssetDatabase.GetAssetPath(asset);
            return string.IsNullOrEmpty(path) ? "Assets" : Path.GetDirectoryName(path).Replace("\\", "/");
        }
    }
}