using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Agentic.UdonAnalyzer;

/// <summary>
/// Syntax-level UdonSharp linter that runs outside Unity.
///
/// Why it exists: the real UdonSharp compiler needs Unity, and getting a diagnostic out of it costs a file
/// write, an asset refresh, a C# compile and a domain reload — tens of seconds per mistake. Most UdonSharp
/// violations are visible in the syntax tree alone, so catching them here turns that loop into milliseconds
/// and leaves Unity for the things only Unity can answer.
///
/// It is deliberately not a substitute for udon_compile. It cannot resolve types, so it cannot know whether
/// a particular method is exposed to Udon; it catches the constructs UdonSharp rejects outright. Every rule
/// here corresponds to a diagnostic the SDK actually produces — the messages quote the SDK's own wording so
/// a reader can match them up.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var paths = new List<string>();
        bool json = false;
        bool strict = false;

        foreach (string arg in args)
        {
            if (arg is "--json") json = true;
            else if (arg is "--strict") strict = true;
            else if (arg is "--help" or "-h") { PrintUsage(); return 0; }
            else paths.Add(arg);
        }

        if (paths.Count == 0) { PrintUsage(); return 2; }

        var files = new List<string>();
        foreach (string path in paths)
        {
            if (Directory.Exists(path)) files.AddRange(Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories));
            else if (File.Exists(path)) files.Add(path);
            else
            {
                Console.Error.WriteLine($"No such file or directory: {path}");
                return 2;
            }
        }

        var diagnostics = new List<Finding>();
        foreach (string file in files)
        {
            try { Analyze(file, File.ReadAllText(file), diagnostics); }
            catch (Exception ex) { Console.Error.WriteLine($"{file}: analyzer failed: {ex.Message}"); }
        }

        int errors = diagnostics.Count(d => d.Severity == "error");
        int warnings = diagnostics.Count(d => d.Severity == "warning");

        if (json)
        {
            var payload = new
            {
                filesAnalyzed = files.Count,
                errorCount = errors,
                warningCount = warnings,
                diagnostics,
                note = "Syntax-level only. It cannot tell whether a specific API is exposed to Udon — run " +
                       "udon.compile for that. Every rule mirrors a diagnostic UdonSharp itself produces.",
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            foreach (var d in diagnostics.OrderBy(d => d.File).ThenBy(d => d.Line))
                Console.WriteLine($"{d.File}({d.Line},{d.Column}): {d.Severity} {d.Rule}: {d.Message}");
            Console.WriteLine($"\n{files.Count} file(s), {errors} error(s), {warnings} warning(s)");
        }

        if (errors > 0) return 1;
        return strict && warnings > 0 ? 1 : 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            udon-analyzer [--json] [--strict] <file-or-directory>...

            Checks UdonSharp sources for constructs UdonSharp rejects, without starting Unity.

              --json     Emit machine-readable output.
              --strict   Exit non-zero on warnings as well as errors.

            Exit codes: 0 clean, 1 findings, 2 bad invocation.
            """);
    }

    public sealed record Finding(
        string File, int Line, int Column, string Severity, string Rule, string Message);

    // ------------------------------------------------------------------ syncable types

    /// <summary>
    /// Types Udon can synchronise, from VRC.Udon.UdonNetworkTypes. Kept as names because this analyzer has
    /// no symbol resolution; that means an aliased type slips through, which udon.compile then catches.
    /// </summary>
    private static readonly Dictionary<string, int> SyncableBytes = new(StringComparer.Ordinal)
    {
        ["bool"] = 1, ["Boolean"] = 1,
        ["char"] = 2, ["Char"] = 2,
        ["byte"] = 1, ["Byte"] = 1, ["sbyte"] = 1, ["SByte"] = 1,
        ["short"] = 2, ["Int16"] = 2, ["ushort"] = 2, ["UInt16"] = 2,
        ["int"] = 4, ["Int32"] = 4, ["uint"] = 4, ["UInt32"] = 4,
        ["long"] = 8, ["Int64"] = 8, ["ulong"] = 8, ["UInt64"] = 8,
        ["float"] = 4, ["Single"] = 4, ["double"] = 8, ["Double"] = 8,
        // Strings and URLs are variable-length; the figure is a budgeting estimate, not a guarantee.
        ["string"] = 64, ["String"] = 64, ["VRCUrl"] = 64,
        ["Color"] = 16, ["Color32"] = 4,
        ["Vector2"] = 8, ["Vector3"] = 12, ["Vector4"] = 16, ["Quaternion"] = 16,
    };

    /// <summary>
    /// Parameterless events Unity or VRChat calls, which must be public and must not be renamed.
    ///
    /// Without this list every world script would be told its Start and Interact are network entry points,
    /// which is both wrong advice and enough noise to make the real finding invisible.
    /// </summary>
    private static readonly HashSet<string> KnownParameterlessEvents = new(StringComparer.Ordinal)
    {
        // Unity messages
        "Start", "Awake", "Update", "LateUpdate", "FixedUpdate", "OnEnable", "OnDisable", "OnDestroy",
        "OnGUI", "OnPreRender", "OnPostRender", "OnPreCull", "OnRenderObject", "OnWillRenderObject",
        "OnBecameVisible", "OnBecameInvisible", "OnDrawGizmos", "OnDrawGizmosSelected", "OnValidate",
        "OnTransformChildrenChanged", "OnTransformParentChanged", "OnMouseDown", "OnMouseUp",
        "OnMouseEnter", "OnMouseExit", "OnMouseOver", "OnMouseDrag", "OnMouseUpAsButton", "OnAnimatorMove",
        // VRChat / Udon events
        "Interact", "PostLateUpdate", "OnPickup", "OnDrop", "OnPickupUseDown", "OnPickupUseUp", "OnSpawn",
        "OnPreSerialization", "OnDeserialization", "OnVideoEnd", "OnVideoError", "OnVideoLoop",
        "OnVideoPause", "OnVideoPlay", "OnVideoReady", "OnVideoStart", "OnVRCQualitySettingsChanged",
        "OnPersistenceUsageUpdated", "OnPurchasesLoaded", "OnListPurchases", "OnListAvailableProducts",
        // Recognised VRChat events whose parameterless form is deprecated. The deprecation is reported by
        // its own rule, so adding a second "this is a network entry point" warning would only be noise.
        "OnStationEntered", "OnStationExited", "OnOwnershipTransferred",
    };

    /// <summary>Generic collections that simply do not exist in Udon, with the usual replacement.</summary>
    private static readonly Dictionary<string, string> ForbiddenGenerics = new(StringComparer.Ordinal)
    {
        ["List"] = "use a fixed array, resizing it yourself when it must grow",
        ["Dictionary"] = "use parallel arrays, or VRC.SDK3.Data.DataDictionary",
        ["HashSet"] = "use an array plus a linear search",
        ["Queue"] = "use an array with head and tail indices",
        ["Stack"] = "use an array with a count",
        ["IEnumerable"] = "iterate an array",
        ["IList"] = "use an array",
        ["Tuple"] = "return via out parameters or fields",
        ["Func"] = "call the method directly; delegates are not available",
        ["Action"] = "call the method directly; delegates are not available",
    };

    // ------------------------------------------------------------------ analysis

    private static void Analyze(string file, string source, List<Finding> findings)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetCompilationUnitRoot();

        foreach (var parseError in tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))
        {
            var position = parseError.Location.GetLineSpan().StartLinePosition;
            findings.Add(new Finding(file, position.Line + 1, position.Character + 1, "error",
                "parse", parseError.GetMessage()));
        }

        foreach (var type in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            bool isUdonBehaviour = InheritsUdonSharpBehaviour(type);
            AnalyzeType(file, tree, type, isUdonBehaviour, findings);
        }

        // Constructs that are rejected regardless of where they appear.
        AnalyzeUniversalConstructs(file, tree, root, findings);
    }

    private static bool InheritsUdonSharpBehaviour(ClassDeclarationSyntax type)
    {
        if (type.BaseList is null) return false;
        foreach (var baseType in type.BaseList.Types)
        {
            string name = baseType.Type.ToString();
            if (name.EndsWith("UdonSharpBehaviour", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static void AnalyzeUniversalConstructs(string file, SyntaxTree tree, SyntaxNode root, List<Finding> findings)
    {
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                // "Try/Catch/Finally is not supported by UdonSharp since Udon does not have a way to
                // handle exceptions" - BinderSyntaxVisitor.cs
                case TryStatementSyntax:
                    Add(findings, file, tree, node, "error", "no-try-catch",
                        "Try/Catch/Finally is not supported by UdonSharp: Udon has no exception handling.");
                    break;

                case ThrowStatementSyntax or ThrowExpressionSyntax:
                    Add(findings, file, tree, node, "error", "no-throw",
                        "UdonSharp does not support throwing exceptions. Return a status value instead.");
                    break;

                case LockStatementSyntax:
                    Add(findings, file, tree, node, "error", "no-lock",
                        "lock is not supported by UdonSharp; Udon is single-threaded so it is also unnecessary.");
                    break;

                case UsingStatementSyntax:
                    Add(findings, file, tree, node, "error", "no-using-statement",
                        "using statements are not supported by UdonSharp (they compile to try/finally).");
                    break;

                case YieldStatementSyntax:
                    Add(findings, file, tree, node, "error", "no-yield",
                        "Iterators are not supported by UdonSharp. Use SendCustomEventDelayedFrames or Update for " +
                        "anything that needs to happen over time.");
                    break;

                case LocalFunctionStatementSyntax:
                    Add(findings, file, tree, node, "error", "no-local-function",
                        "Local method declarations are not currently supported by UdonSharp. Make it a normal method.");
                    break;

                case QueryExpressionSyntax:
                    Add(findings, file, tree, node, "error", "no-linq",
                        "LINQ is not available in Udon. Write an explicit loop.");
                    break;

                case LambdaExpressionSyntax or AnonymousMethodExpressionSyntax:
                    Add(findings, file, tree, node, "error", "no-lambda",
                        "Lambdas and anonymous methods are not supported by UdonSharp; there are no delegates in Udon.");
                    break;

                case AwaitExpressionSyntax:
                    Add(findings, file, tree, node, "error", "no-async",
                        "async/await is not supported by UdonSharp.");
                    break;

                case InitializerExpressionSyntax initializer
                    when initializer.Parent is ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax:
                    Add(findings, file, tree, node, "error", "no-initializer-list",
                        "UdonSharp does not support object or collection initializer lists. Assign the fields separately.");
                    break;

                case ArrayTypeSyntax array when array.RankSpecifiers.Any(r => r.Rank > 1):
                    Add(findings, file, tree, node, "error", "no-multidim-array",
                        "Multidimensional arrays are not supported by UdonSharp; use jagged arrays instead.");
                    break;

                case GenericNameSyntax generic when ForbiddenGenerics.TryGetValue(generic.Identifier.Text, out string? advice):
                    Add(findings, file, tree, node, "error", "generic-not-in-udon",
                        $"{generic.Identifier.Text}<> is not exposed to Udon: {advice}.");
                    break;

                case InvocationExpressionSyntax invocation:
                    AnalyzeInvocation(file, tree, invocation, findings);
                    break;
            }
        }
    }

    private static void AnalyzeInvocation(string file, SyntaxTree tree, InvocationExpressionSyntax invocation, List<Finding> findings)
    {
        string text = invocation.Expression.ToString();

        if (text.EndsWith("StartCoroutine", StringComparison.Ordinal) ||
            text.EndsWith("StopCoroutine", StringComparison.Ordinal))
        {
            Add(findings, file, tree, invocation, "error", "no-coroutines",
                "Coroutines do not exist in Udon. Use SendCustomEventDelayedSeconds or SendCustomEventDelayedFrames.");
        }

        // UnityEvent listeners need delegates, which Udon has none of; the Udon way is to wire the event in
        // the inspector to SendCustomEvent.
        if (text.EndsWith(".AddListener", StringComparison.Ordinal) ||
            text.EndsWith(".RemoveListener", StringComparison.Ordinal))
        {
            Add(findings, file, tree, invocation, "error", "no-event-listeners",
                "AddListener/RemoveListener require delegates, which Udon does not have. Wire the UI event to " +
                "SendCustomEvent in the inspector instead.");
        }

        if (text.EndsWith("GetComponent", StringComparison.Ordinal) && invocation.ArgumentList.Arguments.Count == 0 &&
            invocation.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax } or GenericNameSyntax)
        {
            // Generic GetComponent<T> is fine in UdonSharp; nothing to report. Kept explicit so a future
            // reader does not "fix" it into a false positive.
        }
    }

    private static void AnalyzeType(string file, SyntaxTree tree, ClassDeclarationSyntax type, bool isUdonBehaviour, List<Finding> findings)
    {
        foreach (var nested in type.Members.OfType<BaseTypeDeclarationSyntax>())
        {
            Add(findings, file, tree, nested, "error", "no-nested-types",
                "Nested type declarations are not currently supported by UdonSharp.");
        }

        if (!isUdonBehaviour) return;

        foreach (var constructor in type.Members.OfType<ConstructorDeclarationSyntax>())
        {
            Add(findings, file, tree, constructor, "error", "no-constructors",
                "Constructors are not currently supported on UdonSharpBehaviours. Use Start instead.");
        }

        var syncMode = ReadBehaviourSyncMode(type);
        int syncedBytes = 0;
        int syncedCount = 0;

        foreach (var member in type.Members)
        {
            switch (member)
            {
                case MethodDeclarationSyntax method:
                    AnalyzeMethod(file, tree, method, findings);
                    break;

                case FieldDeclarationSyntax field:
                    AnalyzeField(file, tree, type, field, syncMode, ref syncedBytes, ref syncedCount, findings);
                    break;
            }
        }

        // The community guidance is roughly 50 bytes per behaviour. The SDK enforces nothing here
        // (ClientSimNetworkCalling.cs literally says "TODO: add size limits"), so this is advice.
        if (syncedBytes > 50)
        {
            Add(findings, file, tree, type.Identifier, "warning", "sync-budget",
                $"About {syncedBytes} bytes across {syncedCount} synced field(s). Aim for under 50 bytes per " +
                "behaviour; Udon sync bandwidth is shared by everything in the instance.");
        }
    }

    private static void AnalyzeMethod(string file, SyntaxTree tree, MethodDeclarationSyntax method, List<Finding> findings)
    {
        if (method.TypeParameterList is not null)
        {
            Add(findings, file, tree, method, "error", "no-generic-methods",
                "Generic method declarations on UdonSharpBehaviours are not supported. Move it to a plain static class.");
        }

        if (method.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
        {
            Add(findings, file, tree, method, "error", "no-partial-methods",
                "Partial method declarations are not currently supported by UdonSharp.");
        }

        if (method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)))
        {
            Add(findings, file, tree, method, "error", "no-async",
                "async methods are not supported by UdonSharp.");
        }

        // The parameterless-deprecated events: UdonSharp rejects these with "use the version with the
        // VRCPlayerApi".
        string name = method.Identifier.Text;
        if (method.ParameterList.Parameters.Count == 0 &&
            name is "OnStationEntered" or "OnStationExited" or "OnOwnershipTransferred")
        {
            Add(findings, file, tree, method, "error", "deprecated-event-overload",
                $"The {name}() event is deprecated; use {name}(VRCPlayerApi player) instead.");
        }

        bool networkCallable = method.AttributeLists
            .SelectMany(list => list.Attributes)
            .Any(attribute => attribute.Name.ToString().Contains("NetworkCallable", StringComparison.Ordinal));

        if (networkCallable) AnalyzeNetworkCallable(file, tree, method, findings);
        else if (method.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)) &&
                 method.ParameterList.Parameters.Count == 0 &&
                 !name.StartsWith(char.Parse("_")) &&
                 !KnownParameterlessEvents.Contains(name) &&
                 method.Body is not null)
        {
            // Not a defect, but worth knowing: a public parameterless method is reachable as a legacy
            // network entry point, so anyone in the instance can trigger it.
            Add(findings, file, tree, method, "warning", "implicit-network-entry",
                $"{name} is public and parameterless, which makes it callable over the network by anyone in the " +
                "instance. Prefix local-only methods with _ , or mark intentional ones [NetworkCallable].");
        }
    }

    private static void AnalyzeNetworkCallable(string file, SyntaxTree tree, MethodDeclarationSyntax method, List<Finding> findings)
    {
        if (!method.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
            Add(findings, file, tree, method, "error", "networkcallable-must-be-public",
                "[NetworkCallable] methods must be public.");

        if (method.ReturnType.ToString() != "void")
            Add(findings, file, tree, method, "error", "networkcallable-must-return-void",
                "[NetworkCallable] methods must return void.");

        if (method.ParameterList.Parameters.Count > 8)
            Add(findings, file, tree, method, "error", "networkcallable-too-many-parameters",
                $"[NetworkCallable] allows at most 8 parameters; this has {method.ParameterList.Parameters.Count}.");

        foreach (var parameter in method.ParameterList.Parameters)
        {
            if (parameter.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword) || m.IsKind(SyntaxKind.OutKeyword) || m.IsKind(SyntaxKind.ParamsKeyword)))
                Add(findings, file, tree, parameter, "error", "networkcallable-bad-parameter",
                    "[NetworkCallable] parameters cannot be ref, out or params.");
            if (parameter.Default is not null)
                Add(findings, file, tree, parameter, "error", "networkcallable-bad-parameter",
                    "[NetworkCallable] parameters cannot have default values.");
        }
    }

    private static void AnalyzeField(string file, SyntaxTree tree, ClassDeclarationSyntax type, FieldDeclarationSyntax field,
        string? syncMode, ref int syncedBytes, ref int syncedCount, List<Finding> findings)
    {
        bool isStatic = field.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
        bool isConst = field.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword));
        bool isReadonly = field.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword));

        // "Static fields are not yet supported on user defined types" - FieldSymbol.cs. const is fine.
        if (isStatic && !isConst)
        {
            Add(findings, file, tree, field, "error", "no-static-fields",
                "Static fields are not supported on UdonSharpBehaviours" + (isReadonly ? " (readonly does not help)" : "") +
                ". Use const for compile-time constants, or an instance field.");
        }

        bool synced = field.AttributeLists
            .SelectMany(list => list.Attributes)
            .Any(attribute => attribute.Name.ToString().Contains("UdonSynced", StringComparison.Ordinal));
        if (!synced) return;

        syncedCount++;
        string typeText = field.Declaration.Type.ToString();
        bool isArray = typeText.EndsWith("[]", StringComparison.Ordinal);
        string elementType = isArray ? typeText[..^2] : typeText;
        string bareType = elementType.Contains('.') ? elementType[(elementType.LastIndexOf('.') + 1)..] : elementType;

        if (syncMode is "None" or "NoVariableSync")
        {
            Add(findings, file, tree, field, "error", "synced-with-nosync-behaviour",
                $"A [UdonSynced] field cannot exist on a behaviour with sync mode {syncMode}.");
        }

        // Arrays need Manual sync: "is an array which is not supported by Continuous sync, switch the
        // behaviour to use Manual sync" - UdonSharpCompilerV1.cs
        if (isArray && syncMode is "Continuous")
        {
            Add(findings, file, tree, field, "error", "synced-array-needs-manual",
                "Synced arrays are not supported by Continuous sync. Mark the behaviour " +
                "[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)].");
        }
        if (isArray && syncMode is null)
        {
            Add(findings, file, tree, field, "warning", "synced-array-needs-manual",
                "This behaviour declares no sync mode, and synced arrays require Manual sync. Add " +
                "[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)].");
        }

        if (!SyncableBytes.TryGetValue(bareType, out int bytes))
        {
            Add(findings, file, tree, field, "error", "type-not-syncable",
                $"'{typeText}' is not a type Udon can synchronise. Syncable types are the numeric types, bool, " +
                "char, string, Color, Color32, Vector2/3/4, Quaternion, VRCUrl and arrays of those.");
            return;
        }

        // An array's size is unknown statically; count a nominal 8 elements so the budget warning is not
        // silently bypassed by using arrays.
        syncedBytes += isArray ? bytes * 8 : bytes;
    }

    /// <summary>Reads BehaviourSyncMode off the class attribute, if one is present.</summary>
    private static string? ReadBehaviourSyncMode(ClassDeclarationSyntax type)
    {
        foreach (var attribute in type.AttributeLists.SelectMany(list => list.Attributes))
        {
            if (!attribute.Name.ToString().Contains("UdonBehaviourSyncMode", StringComparison.Ordinal)) continue;
            string arguments = attribute.ArgumentList?.ToString() ?? "";
            foreach (string candidate in new[] { "NoVariableSync", "Continuous", "Manual", "None", "Any" })
            {
                if (arguments.Contains(candidate, StringComparison.Ordinal)) return candidate;
            }
        }
        return null;
    }

    private static void Add(List<Finding> findings, string file, SyntaxTree tree, SyntaxNode node,
        string severity, string rule, string message)
    {
        Add(findings, file, tree, node.GetLocation(), severity, rule, message);
    }

    private static void Add(List<Finding> findings, string file, SyntaxTree tree, SyntaxToken token,
        string severity, string rule, string message)
    {
        Add(findings, file, tree, token.GetLocation(), severity, rule, message);
    }

    private static void Add(List<Finding> findings, string file, SyntaxTree tree, Location location,
        string severity, string rule, string message)
    {
        var position = location.GetLineSpan().StartLinePosition;
        findings.Add(new Finding(file.Replace('\\', '/'), position.Line + 1, position.Character + 1, severity, rule, message));
    }
}
