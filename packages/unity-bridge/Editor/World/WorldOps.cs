using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Agentic.Vrcw.Bridge
{
    /// <summary>
    /// Project and scene setup for VRChat worlds, plus programmatic access to the SDK's validations.
    ///
    /// The recurring theme is bypassing the GUI. Every auto-fix the SDK offers sits behind an
    /// EditorUtility.DisplayDialog confirmation, and a modal dialog is fatal to an unattended run because
    /// it stops the editor's update loop entirely - including the pump this bridge needs to answer at all.
    /// The underlying operations are public statics, so this calls them directly.
    /// </summary>
    public static class WorldOps
    {
        public static void Register()
        {
            BridgeRouter.Register("world.project_status",
                "Whether VRChat layers, the collision matrix and player settings are set up.",
                delegate (Dictionary<string, object> a) { return ProjectStatus(); });

            BridgeRouter.Register("world.fix_project",
                "Apply VRChat's layer and collision-matrix setup, bypassing the SDK's confirmation dialogs. " +
                "Args: layers, collisionMatrix, playerSettings (all default true).",
                delegate (Dictionary<string, object> a) { return FixProject(a); }, defaultTimeoutMs: 120000);

            BridgeRouter.Register("world.scaffold",
                "Create a minimal valid VRChat world scene. Args: path, addFloor, open, overwrite, discardUnsaved.",
                delegate (Dictionary<string, object> a) { return Scaffold(a); }, defaultTimeoutMs: 180000);

            BridgeRouter.Register("world.describe_scene",
                "The scene descriptor's configuration and the scene's root objects.",
                delegate (Dictionary<string, object> a) { return DescribeScene(); });

            BridgeRouter.Register("world.validate",
                "Run the SDK's own pre-build validations and return errors, warnings and stats as data.",
                delegate (Dictionary<string, object> a) { return Validate(a); }, defaultTimeoutMs: 180000);
        }

        // ------------------------------------------------------------------ project setup

        private static Dictionary<string, object> ProjectStatus()
        {
            return new Dictionary<string, object>
            {
                { "layersSetUp", CallUpdateLayersBool("AreLayersSetup") },
                { "collisionMatrixSetUp", CallUpdateLayersBool("IsCollisionLayerMatrixSetup") },
                { "activeBuildTarget", EditorUserBuildSettings.activeBuildTarget.ToString() },
                { "apiCompatibilityLevel", PlayerSettings.GetApiCompatibilityLevel(
                    BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget)).ToString() },
                { "colorSpace", PlayerSettings.colorSpace.ToString() },
                { "lightmapAutoGenerate", UnityEditor.Lightmapping.giWorkflowMode == UnityEditor.Lightmapping.GIWorkflowMode.Iterative }
            };
        }

        private static Dictionary<string, object> FixProject(Dictionary<string, object> a)
        {
            var applied = new List<string>();

            if (Json.GetBool(a, "layers", true) && !CallUpdateLayersBool("AreLayersSetup"))
            {
                // The GUI wraps this in a DisplayDialog (VRCSdkControlPanelWorldBuilder.cs:386).
                Reflect.ResolveType("UpdateLayers")
                    .GetMethod("SetupEditorLayers", BindingFlags.Static | BindingFlags.Public).Invoke(null, null);
                applied.Add("layers");
            }

            if (Json.GetBool(a, "collisionMatrix", true) && !CallUpdateLayersBool("IsCollisionLayerMatrixSetup"))
            {
                Reflect.ResolveType("UpdateLayers")
                    .GetMethod("SetupCollisionLayerMatrix", BindingFlags.Static | BindingFlags.Public).Invoke(null, null);
                applied.Add("collisionMatrix");
            }

            if (Json.GetBool(a, "playerSettings", true))
            {
                UdonReflection.InvokeStatic("VRC.Editor.EnvConfig", "ConfigurePlayerSettings");
                applied.Add("playerSettings");
            }

            var status = ProjectStatus();
            status["applied"] = applied;
            // Both must be true or the build is refused outright: "You must address Layers and Collision
            // Matrix issues before you can build." (VRCSdkControlPanelWorldBuilder.cs:2676)
            status["buildWouldBeBlockedByLayers"] =
                !(bool)status["layersSetUp"] || !(bool)status["collisionMatrixSetUp"];
            return status;
        }

        private static bool CallUpdateLayersBool(string method)
        {
            var type = Reflect.ResolveType("UpdateLayers");
            var info = type.GetMethod(method, BindingFlags.Static | BindingFlags.Public);
            if (info == null) throw new MissingMethodException("VRChat SDK API moved: UpdateLayers." + method);
            return (bool)info.Invoke(null, null);
        }

        // ------------------------------------------------------------------ scaffold

        /// <summary>
        /// Builds a scene that satisfies every hard requirement the SDK checks before a build:
        /// exactly one VRCSceneDescriptor, a PipelineManager on the same GameObject, and at least one
        /// non-null spawn - the last of which is a build-blocking error
        /// (WORLD_SPAWN_EMPTY_ERROR, VRCSdkControlPanelWorldBuilder.cs:957).
        /// </summary>
        private static Dictionary<string, object> Scaffold(Dictionary<string, object> a)
        {
            string path = UdonReflection.NormalizePath(Json.GetString(a, "path", "Assets/Scenes/World.unity"));
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("path must be under Assets/");
            if (!path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) path += ".unity";

            bool overwrite = Json.GetBool(a, "overwrite", false);
            if (File.Exists(path) && !overwrite)
                throw new ArgumentException("A scene already exists at \"" + path + "\". Pass overwrite:true to replace it.");

            // Creating a new scene discards whatever is open. Losing a user's unsaved work silently would
            // be unacceptable for an unattended agent, so it is opt-in.
            var dirty = new List<string>();
            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var open = EditorSceneManager.GetSceneAt(i);
                if (open.isDirty) dirty.Add(string.IsNullOrEmpty(open.path) ? open.name + " (unsaved)" : open.path);
            }
            if (dirty.Count > 0 && !Json.GetBool(a, "discardUnsaved", false))
                throw new ArgumentException(
                    "These open scenes have unsaved changes: " + string.Join(", ", dirty.ToArray()) +
                    ". Save them, or pass discardUnsaved:true to lose those changes.");

            string folder = path.Substring(0, path.LastIndexOf('/'));
            EnsureAssetFolder(folder);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            var descriptorType = Reflect.ResolveType("VRC.SDK3.Components.VRCSceneDescriptor");
            var pipelineType = Reflect.ResolveType("VRC.Core.PipelineManager");

            var worldRoot = new GameObject("VRCWorld");
            worldRoot.transform.position = Vector3.zero;
            var descriptor = worldRoot.AddComponent(descriptorType);
            worldRoot.AddComponent(pipelineType); // must share the GameObject with the descriptor

            var camera = Camera.main != null ? Camera.main.gameObject : GameObject.Find("Main Camera");

            SetField(descriptor, "spawns", new[] { worldRoot.transform });
            SetField(descriptor, "spawnRadius", 0.2f);
            SetField(descriptor, "RespawnHeightY", -100f);
            SetField(descriptor, "ReferenceCamera", camera);
            SetField(descriptor, "unityVersion", Application.unityVersion);

            if (Json.GetBool(a, "addFloor", true))
            {
                var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
                floor.name = "Floor";
                floor.transform.position = new Vector3(0f, -0.5f, 0f);
                floor.transform.localScale = new Vector3(30f, 1f, 30f);
            }

            EditorSceneManager.MarkSceneDirty(scene);
            bool saved = EditorSceneManager.SaveScene(scene, path);

            // Deliberately NOT AssetDatabase.ImportAsset(path) here. SaveScene already registers the
            // asset, and re-importing the scene that is currently open makes Unity believe it changed on
            // disk behind its back: it raises "The open scene(s) have been modified externally", a modal
            // dialog that freezes the editor's update loop and with it this bridge. That was hit twice
            // during development before the cause was clear.

            var result = DescribeScene();
            result["scenePath"] = path;
            result["saved"] = saved;
            result["createdFloor"] = Json.GetBool(a, "addFloor", true);
            result["referenceCameraAssigned"] = camera != null;
            return result;
        }

        private static void EnsureAssetFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string[] parts = folder.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        private static void SetField(object target, string name, object value)
        {
            var field = target.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (field == null) throw new MissingMemberException("VRChat SDK API moved: no field " + name + " on " + target.GetType().Name);
            field.SetValue(target, value);
            EditorUtility.SetDirty((UnityEngine.Object)target);
        }

        // ------------------------------------------------------------------ describe

        private static Dictionary<string, object> DescribeScene()
        {
            var descriptorType = Reflect.ResolveType("VRC.SDKBase.VRC_SceneDescriptor");
            var descriptors = UnityEngine.Object.FindObjectsOfType(descriptorType, true);

            var described = new List<object>();
            foreach (var obj in descriptors)
            {
                var component = obj as Component;
                if (component == null) continue;

                var spawns = GetField(component, "spawns") as Transform[];
                var validSpawns = new List<object>();
                if (spawns != null)
                {
                    foreach (var t in spawns)
                    {
                        if (t == null) continue;
                        validSpawns.Add(new Dictionary<string, object>
                        {
                            { "path", UdonOps.HierarchyPath(t) },
                            { "position", t.position }
                        });
                    }
                }

                var pipelineType = Reflect.ResolveType("VRC.Core.PipelineManager");
                var pipeline = component.GetComponent(pipelineType);

                described.Add(new Dictionary<string, object>
                {
                    { "gameObject", UdonOps.HierarchyPath(component.transform) },
                    { "type", component.GetType().FullName },
                    { "spawnCount", spawns == null ? 0 : spawns.Length },
                    { "validSpawnCount", validSpawns.Count },
                    { "spawns", validSpawns },
                    { "referenceCamera", GetField(component, "ReferenceCamera") },
                    { "respawnHeightY", GetField(component, "RespawnHeightY") },
                    { "forbidUserPortals", GetField(component, "ForbidUserPortals") },
                    { "hasPipelineManagerOnSameObject", pipeline != null },
                    { "blueprintId", pipeline == null ? null : GetField(pipeline, "blueprintId") }
                });
            }

            var roots = new List<object>();
            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var scene = EditorSceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    roots.Add(new Dictionary<string, object>
                    {
                        { "name", root.name },
                        { "active", root.activeSelf },
                        { "childCount", root.transform.childCount }
                    });
                }
            }

            // More than one descriptor is a build-blocking error (VRCSdkControlPanelWorldBuilder.cs:233).
            var result = new Dictionary<string, object>
            {
                { "descriptorCount", described.Count },
                { "descriptors", described },
                { "roots", roots }
            };
            var problems = new List<string>();
            if (described.Count == 0) problems.Add("No VRCSceneDescriptor: this is not a VRChat world scene yet.");
            if (described.Count > 1) problems.Add("More than one VRCSceneDescriptor; the SDK refuses to build.");
            foreach (Dictionary<string, object> d in described)
            {
                if (Convert.ToInt32(d["validSpawnCount"]) == 0) problems.Add("Descriptor has no valid spawn point; the SDK refuses to build.");
                if (!(bool)d["hasPipelineManagerOnSameObject"]) problems.Add("PipelineManager is missing from the descriptor's GameObject.");
            }
            result["problems"] = problems;
            return result;
        }

        private static object GetField(object target, string name)
        {
            var field = target.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return field == null ? null : field.GetValue(target);
        }

        // ------------------------------------------------------------------ validate

        /// <summary>
        /// Drives the SDK's own validation pass and reads the results out as data.
        ///
        /// There is no API for this; the validations exist only to paint a GUI. The sequence that works:
        /// the control panel window must exist (TryGetBuilder refuses otherwise), IsValidBuilder must be
        /// called because that is what populates the builder's scene list, and CreateValidationsGUI is
        /// what actually runs the checks - into a throwaway VisualElement here.
        ///
        /// Only errors have a public getter. Warnings, infos and stats live in private dictionaries, so
        /// they are read reflectively; without them an agent would miss everything short of a hard error.
        /// </summary>
        private static Dictionary<string, object> Validate(Dictionary<string, object> a)
        {
            var panelType = Reflect.ResolveType("VRCSdkControlPanel");
            var windowField = panelType.GetField("window", BindingFlags.Static | BindingFlags.Public);
            var panel = windowField == null ? null : windowField.GetValue(null) as EditorWindow;

            if (panel == null && Json.GetBool(a, "openPanel", true))
            {
                EditorApplication.ExecuteMenuItem("VRChat SDK/Show Control Panel");
                panel = windowField == null ? null : windowField.GetValue(null) as EditorWindow;
            }

            if (panel == null)
            {
                return NotReady("The SDK control panel window is not open, and opening it did not take effect. " +
                                "TryGetBuilder refuses to return a builder without it.");
            }

            var builderApiType = Reflect.ResolveType("VRC.SDK3.Editor.IVRCSdkWorldBuilderApi");
            var tryGet = panelType.GetMethod("TryGetBuilder", BindingFlags.Static | BindingFlags.Public);
            if (tryGet == null) throw new MissingMethodException("VRChat SDK API moved: VRCSdkControlPanel.TryGetBuilder");

            var generic = tryGet.MakeGenericMethod(builderApiType);
            var callArgs = new object[] { null };
            bool gotBuilder = (bool)generic.Invoke(null, callArgs);
            object builder = callArgs[0];

            if (!gotBuilder || builder == null)
            {
                bool loggedIn = IsLoggedIn();
                return NotReady(loggedIn
                    ? "The SDK panel is open but exposed no world builder. If the V3 pipeline is enabled it takes priority."
                    : "Not signed in. The SDK panel only registers builders while APIUser.IsLoggedIn " +
                      "(VRCSdkControlPanelBuilder.cs:919), so validation and builds need the one-time sign-in " +
                      "in the SDK control panel.");
            }

            string builderMessage;
            var isValid = builder.GetType().GetMethod("IsValidBuilder", new[] { typeof(string).MakeByRefType() });
            var validArgs = new object[] { null };
            bool builderValid = isValid != null && (bool)isValid.Invoke(builder, validArgs);
            builderMessage = validArgs[0] as string;

            panelType.GetMethod("ResetIssues", BindingFlags.Instance | BindingFlags.Public).Invoke(panel, null);

            string validationError = null;
            if (builderValid)
            {
                try
                {
                    var createValidations = builder.GetType().GetMethod("CreateValidationsGUI", new[] { typeof(VisualElement) });
                    if (createValidations != null) createValidations.Invoke(builder, new object[] { new VisualElement() });
                    var checkedProp = panelType.GetProperty("CheckedForIssues", BindingFlags.Instance | BindingFlags.Public);
                    if (checkedProp != null && checkedProp.CanWrite) checkedProp.SetValue(panel, true, null);
                }
                catch (TargetInvocationException tie)
                {
                    validationError = (tie.InnerException ?? tie).Message;
                }
            }

            var subjects = new List<object> { panel };
            var descriptorType = Reflect.ResolveType("VRC.SDKBase.VRC_SceneDescriptor");
            foreach (var d in UnityEngine.Object.FindObjectsOfType(descriptorType, true)) subjects.Add(d);

            var errors = ReadIssueList(panelType, panel, subjects);
            var result = new Dictionary<string, object>
            {
                { "ready", true },
                { "builderType", builder.GetType().FullName },
                { "builderValid", builderValid },
                { "builderMessage", builderMessage },
                { "loggedIn", IsLoggedIn() },
                { "errors", errors },
                { "errorCount", errors.Count },
                { "warnings", ReadPrivateIssueDict(panelType, panel, "GUIWarnings") },
                { "infos", ReadPrivateIssueDict(panelType, panel, "GUIInfos") },
                { "stats", ReadPrivateIssueDict(panelType, panel, "GUIStats") }
            };
            if (validationError != null) result["validationError"] = validationError;
            result["buildWouldBeBlocked"] = errors.Count > 0 || !builderValid;
            return result;
        }

        private static Dictionary<string, object> NotReady(string why)
        {
            return new Dictionary<string, object>
            {
                { "ready", false },
                { "reason", why },
                { "loggedIn", IsLoggedIn() },
                { "errors", new List<object>() },
                { "errorCount", 0 },
                { "buildWouldBeBlocked", true }
            };
        }

        private static bool IsLoggedIn()
        {
            try
            {
                var apiUser = Reflect.ResolveType("VRC.Core.APIUser");
                var prop = apiUser.GetProperty("IsLoggedIn", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                return prop != null && (bool)prop.GetValue(null, null);
            }
            catch (Exception) { return false; }
        }

        private static List<object> ReadIssueList(Type panelType, object panel, List<object> subjects)
        {
            var collected = new List<object>();
            var getIssues = panelType.GetMethod("GetGuiErrorsOrIssuesForItem", BindingFlags.Instance | BindingFlags.Public);
            if (getIssues == null) return collected;

            foreach (object subject in subjects)
            {
                object list;
                try { list = getIssues.Invoke(panel, new[] { subject }); }
                catch (TargetInvocationException) { continue; }

                var enumerable = list as System.Collections.IEnumerable;
                if (enumerable == null) continue;
                foreach (object issue in enumerable) collected.Add(DescribeIssue(subject, issue));
            }
            return collected;
        }

        private static List<object> ReadPrivateIssueDict(Type panelType, object panel, string fieldName)
        {
            var collected = new List<object>();
            var field = panelType.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) return collected;

            var dict = field.GetValue(panel) as System.Collections.IDictionary;
            if (dict == null) return collected;

            foreach (System.Collections.DictionaryEntry entry in dict)
            {
                var enumerable = entry.Value as System.Collections.IEnumerable;
                if (enumerable == null) continue;
                foreach (object issue in enumerable) collected.Add(DescribeIssue(entry.Key, issue));
            }
            return collected;
        }

        private static Dictionary<string, object> DescribeIssue(object subject, object issue)
        {
            string text = null;
            string rating = null;
            bool hasFix = false;

            if (issue != null)
            {
                var type = issue.GetType();
                var textField = type.GetField("issueText", BindingFlags.Instance | BindingFlags.Public);
                if (textField != null) text = textField.GetValue(issue) as string;

                var ratingField = type.GetField("performanceRating", BindingFlags.Instance | BindingFlags.Public);
                if (ratingField != null)
                {
                    object value = ratingField.GetValue(issue);
                    if (value != null) rating = value.ToString();
                }

                var fixField = type.GetField("fixThisIssue", BindingFlags.Instance | BindingFlags.Public);
                hasFix = fixField != null && fixField.GetValue(issue) != null;
            }

            var subjectObject = subject as UnityEngine.Object;
            return new Dictionary<string, object>
            {
                { "text", text },
                { "subject", subjectObject == null ? null : subjectObject.name },
                { "subjectType", subject == null ? null : subject.GetType().FullName },
                { "performanceRating", rating },
                { "sdkOffersAutoFix", hasFix }
            };
        }
    }
}
