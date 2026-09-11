using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Agentic.Vrcw.Bridge
{
    /// <summary>
    /// One call that answers "what is the editor looking at right now", including the two VRChat
    /// preconditions an autonomous run depends on:
    ///   - the SDK control panel window must exist, or TryGetBuilder fails outright
    ///     ("Cannot get builder, SDK window is not open", VRCSdkControlPanelBuilder.cs:497)
    ///   - the panel only registers builders while APIUser.IsLoggedIn (VRCSdkControlPanelBuilder.cs:919)
    ///
    /// Both are read reflectively and each is guarded independently, so an SDK rename degrades this
    /// probe to "unknown" rather than breaking the bridge assembly's compile.
    /// </summary>
    public static class EditorStateProbe
    {
        public static Dictionary<string, object> Read()
        {
            return new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "projectPath", BridgePaths.ProjectRoot.Replace('\\', '/') },
                { "activeBuildTarget", EditorUserBuildSettings.activeBuildTarget.ToString() },
                { "selectedBuildTargetGroup", EditorUserBuildSettings.selectedBuildTargetGroup.ToString() },
                { "isPlaying", EditorApplication.isPlaying },
                { "isCompiling", EditorApplication.isCompiling },
                { "isUpdatingAssets", EditorApplication.isUpdating },
                { "scenes", ReadScenes() },
                { "selection", ReadSelection() },
                { "vrchat", ReadVRChatState() }
            };
        }

        private static List<object> ReadScenes()
        {
            var scenes = new List<object>();
            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                Scene scene = EditorSceneManager.GetSceneAt(i);
                scenes.Add(new Dictionary<string, object>
                {
                    { "name", scene.name },
                    { "path", scene.path },
                    { "isLoaded", scene.isLoaded },
                    { "isDirty", scene.isDirty },
                    { "rootCount", scene.isLoaded ? scene.rootCount : 0 }
                });
            }
            return scenes;
        }

        private static List<object> ReadSelection()
        {
            var selected = new List<object>();
            var objects = Selection.objects;
            for (int i = 0; i < objects.Length && i < 50; i++)
            {
                var o = objects[i];
                if (o == null) continue;
                selected.Add(new Dictionary<string, object>
                {
                    { "name", o.name },
                    { "type", o.GetType().FullName },
                    { "instanceId", o.GetInstanceID() }
                });
            }
            return selected;
        }

        private static Dictionary<string, object> ReadVRChatState()
        {
            var state = new Dictionary<string, object>();

            state["sdkPanelOpen"] = Probe(delegate
            {
                var panelType = Reflect.ResolveType("VRCSdkControlPanel");
                return Resources.FindObjectsOfTypeAll(panelType).Length > 0;
            });

            state["loggedIn"] = Probe(delegate
            {
                var apiUser = Reflect.ResolveType("VRC.Core.APIUser");
                var prop = apiUser.GetProperty("IsLoggedIn", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                return prop == null ? null : prop.GetValue(null, null);
            });

            state["dryRun"] = Probe(delegate { return SessionState.GetBool("VRC.SDKBase.DryRun", false); });

            state["sceneDescriptorCount"] = Probe(delegate
            {
                var descriptorType = Reflect.ResolveType("VRC.SDKBase.VRC_SceneDescriptor");
                return Resources.FindObjectsOfTypeAll(descriptorType).Length;
            });

            return state;
        }

        /// <summary>Returns the probe's value, or a diagnostic string — never throws.</summary>
        private static object Probe(Func<object> read)
        {
            try { return read(); }
            catch (Exception ex) { return "unavailable: " + ex.GetType().Name; }
        }
    }
}
