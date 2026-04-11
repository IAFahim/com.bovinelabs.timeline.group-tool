using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

public static class TimelineGroupTool
{
    const string ContextMenu = "CONTEXT/PlayableDirector/";

    [MenuItem(ContextMenu + "Split Groups")]
    static void SplitFromInspector(MenuCommand command)
    {
        if (command.context is PlayableDirector director)
            Expand(director);
    }

    [MenuItem(ContextMenu + "Split Groups", true)]
    static bool CanSplitInspector(MenuCommand command)
    {
        return command.context is PlayableDirector d && d.playableAsset is TimelineAsset tl
            && tl.GetRootTracks().Any(t => t is GroupTrack);
    }

    [MenuItem(ContextMenu + "Join Timelines")]
    static void JoinFromInspector(MenuCommand command)
    {
        var selected = Selection.gameObjects
            .Select(go => go.GetComponent<PlayableDirector>())
            .Where(d => d != null && d.playableAsset is TimelineAsset)
            .ToArray();

        if (selected.Length >= 2)
            Collapse(selected);
    }

    [MenuItem(ContextMenu + "Join Timelines", true)]
    static bool CanJoinInspector(MenuCommand command)
    {
        return command.context is PlayableDirector
            && Selection.gameObjects.Length >= 2
            && Selection.gameObjects.All(go => go.GetComponent<PlayableDirector>()?.playableAsset is TimelineAsset);
    }

    public static void Expand(PlayableDirector source)
    {
        var sourceAsset = source.playableAsset as TimelineAsset;
        if (sourceAsset == null) return;

        var directory = AssetDirectory(sourceAsset);
        var bindings = CollectBindings(source);
        var groups = sourceAsset.GetRootTracks().Where(g => g is GroupTrack).ToList();

        if (groups.Count == 0)
        {
            Debug.LogWarning("[TimelineGroupTool] No GroupTracks found in timeline.");
            return;
        }

        var parent = source.gameObject.transform.parent;
        var siblingIndex = source.gameObject.transform.GetSiblingIndex();

        foreach (var group in groups)
        {
            var children = group.GetChildTracks().ToList();
            if (children.Count == 0) continue;

            var (asset, trackMap) = CreateExpandedTimeline(children, group.name);
            var path = AssetDatabase.GenerateUniqueAssetPath($"{directory}/{group.name}.playable");

            AssetDatabase.CreateAsset(asset, path);

            foreach (var child in children)
                CloneClips(child, trackMap[child], asset);

            AssetDatabase.SaveAssets();

            var go = new GameObject(group.name);
            Undo.RegisterCreatedObjectUndo(go, "Split Groups");
            go.transform.SetParent(parent);
            go.transform.SetSiblingIndex(++siblingIndex);

            var dir = go.AddComponent<PlayableDirector>();
            dir.playableAsset = asset;
            ApplyBindings(dir, bindings, trackMap);
        }

        EditorSceneManager.SaveOpenScenes();
    }

    public static void Collapse(params PlayableDirector[] sources)
    {
        var valid = sources.Where(s => s?.playableAsset is TimelineAsset).ToArray();
        if (valid.Length == 0) return;

        var directory = CommonDirectory(valid);
        var (merged, allBindings) = CreateCollapsedTimeline(valid);
        var path = AssetDatabase.GenerateUniqueAssetPath($"{directory}/Timeline.playable");

        AssetDatabase.CreateAsset(merged, path);

        var groupList = merged.GetRootTracks().ToList();
        for (int i = 0; i < groupList.Count && i < valid.Length; i++)
        {
            var sourceTracks = ((TimelineAsset)valid[i].playableAsset).GetRootTracks().ToList();
            var childTracks = groupList[i].GetChildTracks().ToList();

            for (int j = 0; j < sourceTracks.Count && j < childTracks.Count; j++)
                CloneClips(sourceTracks[j], childTracks[j], merged);
        }

        AssetDatabase.SaveAssets();

        var parent = valid[0].gameObject.transform.parent;
        var siblingIndex = valid[0].gameObject.transform.GetSiblingIndex();

        var mergedGO = new GameObject("Timeline");
        Undo.RegisterCreatedObjectUndo(mergedGO, "Join Timelines");
        mergedGO.transform.SetParent(parent);
        mergedGO.transform.SetSiblingIndex(siblingIndex);

        var mergedDir = mergedGO.AddComponent<PlayableDirector>();
        mergedDir.playableAsset = merged;

        foreach (var kv in allBindings)
            if (kv.Value != null)
                mergedDir.SetGenericBinding(kv.Key, kv.Value);

        var assetPaths = valid
            .Select(s => AssetDatabase.GetAssetPath(s.playableAsset))
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct()
            .ToList();

        foreach (var source in valid)
            Undo.DestroyObjectImmediate(source.gameObject);

        foreach (var assetPath in assetPaths)
            AssetDatabase.DeleteAsset(assetPath);

        AssetDatabase.SaveAssets();
        EditorSceneManager.SaveOpenScenes();

        Selection.activeGameObject = mergedGO;
    }

    static (TimelineAsset asset, Dictionary<TrackAsset, TrackAsset> map) CreateExpandedTimeline(
        List<TrackAsset> children, string name)
    {
        var asset = ScriptableObject.CreateInstance<TimelineAsset>();
        asset.name = name;

        var map = new Dictionary<TrackAsset, TrackAsset>();
        foreach (var child in children)
            map[child] = asset.CreateTrack(child.GetType(), null, child.name);

        return (asset, map);
    }

    static (TimelineAsset asset, Dictionary<TrackAsset, UnityEngine.Object> bindings) CreateCollapsedTimeline(
        PlayableDirector[] sources)
    {
        var merged = ScriptableObject.CreateInstance<TimelineAsset>();
        merged.name = "Timeline";

        var allBindings = new Dictionary<TrackAsset, UnityEngine.Object>();

        foreach (var source in sources)
        {
            var sourceAsset = (TimelineAsset)source.playableAsset;
            var group = merged.CreateTrack<GroupTrack>(null);
            group.name = sourceAsset.name;

            var sourceBindings = CollectBindings(source);
            foreach (var track in sourceAsset.GetRootTracks())
            {
                var cloned = merged.CreateTrack(track.GetType(), group, track.name);
                if (sourceBindings.TryGetValue(track, out var binding))
                    allBindings[cloned] = binding;
            }
        }

        return (merged, allBindings);
    }

    static void CloneClips(TrackAsset source, TrackAsset target, TimelineAsset targetAsset)
    {
        foreach (var clip in source.GetClips())
        {
            var newClip = InvokeCreateDefaultClip(target);
            if (newClip == null) continue;

            CloneClipAsset(clip, newClip, targetAsset);
            CopyClipProperties(clip, newClip);
        }
    }

    static void CloneClipAsset(TimelineClip source, TimelineClip target, TimelineAsset targetAsset)
    {
        if (source.asset == null) return;

        var clonedAsset = UnityEngine.Object.Instantiate(source.asset);
        clonedAsset.name = source.asset.name;

        if (target.asset != null)
        {
            if (AssetDatabase.IsSubAsset(target.asset))
                AssetDatabase.RemoveObjectFromAsset(target.asset);
            UnityEngine.Object.DestroyImmediate(target.asset);
        }

        AssetDatabase.AddObjectToAsset(clonedAsset, targetAsset);
        target.asset = clonedAsset;
    }

    static void CopyClipProperties(TimelineClip source, TimelineClip target)
    {
        target.start = source.start;
        target.duration = source.duration;
        target.displayName = source.displayName;
        target.clipIn = source.clipIn;
        target.timeScale = source.timeScale;
    }

    static TimelineClip InvokeCreateDefaultClip(TrackAsset track)
    {
        var method = track.GetType().GetMethod("CreateDefaultClip",
            BindingFlags.Public | BindingFlags.Instance,
            null, Type.EmptyTypes, null);
        return method?.Invoke(track, null) as TimelineClip;
    }

    static Dictionary<TrackAsset, UnityEngine.Object> CollectBindings(PlayableDirector director)
    {
        var result = new Dictionary<TrackAsset, UnityEngine.Object>();
        if (director.playableAsset is not TimelineAsset asset) return result;

        foreach (var track in asset.GetOutputTracks())
            result[track] = director.GetGenericBinding(track);

        return result;
    }

    static void ApplyBindings(
        PlayableDirector target,
        Dictionary<TrackAsset, UnityEngine.Object> sourceBindings,
        Dictionary<TrackAsset, TrackAsset> trackMap)
    {
        foreach (var kv in trackMap)
        {
            if (sourceBindings.TryGetValue(kv.Key, out var binding) && binding != null)
                target.SetGenericBinding(kv.Value, binding);
        }
    }

    static string AssetDirectory(UnityEngine.Object asset)
    {
        var path = AssetDatabase.GetAssetPath(asset);
        return string.IsNullOrEmpty(path) ? "Assets" : Path.GetDirectoryName(path);
    }

    static string CommonDirectory(PlayableDirector[] directors)
    {
        foreach (var d in directors)
        {
            var dir = AssetDirectory(d.playableAsset);
            if (dir != "Assets") return dir;
        }
        return "Assets";
    }
}
