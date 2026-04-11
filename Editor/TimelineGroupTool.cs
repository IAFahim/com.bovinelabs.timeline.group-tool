using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;
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

            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();

            var directory = GetDirectory(sourceAsset);
            var parent = source.transform.parent;
            var siblingIndex = source.transform.GetSiblingIndex();
            var groups = sourceAsset.GetRootTracks().Where(t => t is GroupTrack).ToList();

            try
            {
                AssetDatabase.StartAssetEditing();
                for (int i = 0; i < groups.Count; i++)
                {
                    var group = (GroupTrack)groups[i];
                    EditorUtility.DisplayProgressBar("Splitting Timeline", group.name, i / (float)groups.Count);

                    var newAsset = CloneTimelineWithOnly(group);
                    var path = AssetDatabase.GenerateUniqueAssetPath(
                        $"{directory}/{sourceAsset.name}_{group.name}.playable");
                    AssetDatabase.CreateAsset(newAsset, path);

                    var go = new GameObject(group.name);
                    Undo.RegisterCreatedObjectUndo(go, "Split Groups");
                    go.transform.SetParent(parent);
                    go.transform.SetSiblingIndex(++siblingIndex);

                    var dir = go.AddComponent<PlayableDirector>();
                    Undo.RegisterCreatedObjectUndo(dir, "Split Groups");
                    dir.playableAsset = newAsset;
                    CopyDirectorSettings(source, dir);
                    TransferBindings(source, dir, group);
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
                EditorSceneManager.MarkSceneDirty(source.gameObject.scene);
                Undo.CollapseUndoOperations(undoGroup);
            }
        }

        public static void Collapse(params PlayableDirector[] sources)
        {
            if (sources.Length < 2) return;
            if (!EditorUtility.DisplayDialog("Join Timelines",
                $"Merge {sources.Length} timelines into one? Sources will be moved to backup, not deleted.",
                "Join", "Cancel")) return;

            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();

            var first = sources[0];
            var directory = GetDirectory(first.playableAsset);
            var merged = ScriptableObject.CreateInstance<TimelineAsset>();
            merged.name = $"{first.gameObject.name}_Merged";

            var bindingMap = new Dictionary<TrackAsset, Object>();

            try
            {
                AssetDatabase.StartAssetEditing();
                foreach (var src in sources)
                {
                    var srcAsset = (TimelineAsset)src.playableAsset;
                    var group = merged.CreateTrack<GroupTrack>(null, srcAsset.name);

                    foreach (var rootTrack in srcAsset.GetRootTracks())
                    {
                        var clone = CloneTrackRecursive(rootTrack, merged, group);
                        var binding = src.GetGenericBinding(rootTrack);
                        if (binding != null) bindingMap[clone] = binding;

                        foreach (var child in rootTrack.GetChildTracks())
                            TransferChildBindings(src, child, bindingMap);
                    }
                }

                var path = AssetDatabase.GenerateUniqueAssetPath($"{directory}/{merged.name}.playable");
                AssetDatabase.CreateAsset(merged, path);

                var parent = first.transform.parent;
                var siblingIndex = first.transform.GetSiblingIndex();
                var go = new GameObject(merged.name);
                Undo.RegisterCreatedObjectUndo(go, "Join Timelines");
                go.transform.SetParent(parent);
                go.transform.SetSiblingIndex(siblingIndex);

                var dir = go.AddComponent<PlayableDirector>();
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
                AssetDatabase.SaveAssets();
                EditorSceneManager.MarkSceneDirty(first.gameObject.scene);
                Undo.CollapseUndoOperations(undoGroup);
            }
        }

        static TimelineAsset CloneTimelineWithOnly(GroupTrack sourceGroup)
        {
            var temp = ScriptableObject.CreateInstance<TimelineAsset>();
            temp.name = sourceGroup.name;
            foreach (var child in sourceGroup.GetChildTracks())
                CloneTrackRecursive(child, temp, null);
            return temp;
        }

        static TrackAsset CloneTrackRecursive(TrackAsset source, TimelineAsset dest, TrackAsset parent)
        {
            var clone = dest.CreateTrack(source.GetType(), parent, source.name);

            clone.muted = source.muted;
            clone.locked = source.locked;
            if (source is AnimationTrack animSrc && clone is AnimationTrack animDst)
                EditorUtility.CopySerialized(animSrc, animDst);

            foreach (var clip in source.GetClips())
            {
                var newClip = clone.CreateDefaultClip();
                if (clip.asset != null)
                {
                    var newPlayableAsset = Object.Instantiate(clip.asset);
                    newPlayableAsset.name = clip.asset.name;
                    newClip.asset = newPlayableAsset;
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
                CloneTrackRecursive(child, dest, clone);

            return clone;
        }

        static void TransferBindings(PlayableDirector src, PlayableDirector dst, GroupTrack group)
        {
            foreach (var track in group.GetChildTracks())
            {
                var binding = src.GetGenericBinding(track);
                var dstAsset = (TimelineAsset)dst.playableAsset;
                var dstTrack = dstAsset.GetOutputTracks().FirstOrDefault(t => t.name == track.name);
                if (dstTrack != null && binding != null)
                    dst.SetGenericBinding(dstTrack, binding);
            }
        }

        static void TransferChildBindings(PlayableDirector src, TrackAsset track, Dictionary<TrackAsset, Object> map)
        {
            var binding = src.GetGenericBinding(track);
            if (binding != null) map[track] = binding;
            foreach (var child in track.GetChildTracks())
                TransferChildBindings(src, child, map);
        }

        static void CopyDirectorSettings(PlayableDirector src, PlayableDirector dst)
        {
            dst.timeUpdateMode = src.timeUpdateMode;
            dst.extrapolationMode = src.extrapolationMode;
            dst.playOnAwake = false;
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
                    AssetDatabase.MoveAsset(path, $"{backupDir}/{fileName}");
                }

                Undo.RegisterCompleteObjectUndo(src.gameObject, "Backup");
                src.gameObject.SetActive(false);
            }
        }

        static string GetDirectory(Object asset)
        {
            var path = AssetDatabase.GetAssetPath(asset);
            return string.IsNullOrEmpty(path) ? "Assets" : Path.GetDirectoryName(path).Replace("\\", "/");
        }
    }
}
