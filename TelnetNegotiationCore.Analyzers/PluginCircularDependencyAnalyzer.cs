using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace TelnetNegotiationCore.Analyzers;

/// <summary>
/// Analyzer that detects circular dependencies between plugins at compile time.
/// Rule TNCP002: Plugins must not have circular dependencies
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class PluginCircularDependencyAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "TNCP002";

    private static readonly LocalizableString Title = "Circular dependency detected in plugin dependencies";
    private static readonly LocalizableString MessageFormat =
        "Plugin '{0}' has a circular dependency: {1}";
    private static readonly LocalizableString Description =
        "Plugins must not have circular dependencies. The dependency chain creates a cycle that prevents proper initialization order.";

    private const string Category = "TelnetPlugin";

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
        DiagnosticId,
        Title,
        MessageFormat,
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: Description,
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(compilationStartContext =>
        {
            var pluginInterface = compilationStartContext.Compilation.GetTypeByMetadataName("TelnetNegotiationCore.Plugins.ITelnetProtocolPlugin");
            if (pluginInterface == null)
                return;

            // Use a thread-safe collection to store plugin information
            var plugins = new System.Collections.Concurrent.ConcurrentBag<(INamedTypeSymbol Symbol, Location Location)>();

            // Register symbol action to collect all plugin types
            compilationStartContext.RegisterSymbolAction(symbolContext =>
            {
                if (symbolContext.Symbol is INamedTypeSymbol namedType &&
                    namedType.TypeKind == TypeKind.Class &&
                    !namedType.IsAbstract &&
                    ImplementsInterface(namedType, pluginInterface))
                {
                    var location = namedType.Locations.FirstOrDefault();
                    if (location != null)
                    {
                        plugins.Add((namedType, location));
                    }
                }
            }, SymbolKind.NamedType);

            // Register compilation end action to analyze all collected plugins
            compilationStartContext.RegisterCompilationEndAction(compilationEndContext =>
            {
                AnalyzePluginDependencies(compilationEndContext, plugins.ToList());
            });
        });
    }

    private static void AnalyzePluginDependencies(
        CompilationAnalysisContext context,
        List<(INamedTypeSymbol Symbol, Location Location)> plugins)
    {
        // Build dependency graph
        var dependencyMap = new Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>>(SymbolEqualityComparer.Default);

        foreach (var (symbol, _) in plugins)
        {
            var dependencies = GetDependencies(symbol);
            dependencyMap[symbol] = dependencies;
        }

        // Detect circular dependencies
        foreach (var (symbol, location) in plugins)
        {
            var visited = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            var path = new List<string>();

            if (HasCircularDependency(symbol, dependencyMap, visited, path))
            {
                var cycle = string.Join(" → ", path) + " → " + symbol.Name;
                var diagnostic = Diagnostic.Create(
                    Rule,
                    location,
                    symbol.Name,
                    cycle);

                context.ReportDiagnostic(diagnostic);
            }
        }
    }

    private static bool HasCircularDependency(
        INamedTypeSymbol current,
        Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>> dependencyMap,
        HashSet<INamedTypeSymbol> visited,
        List<string> path)
    {
        if (visited.Contains(current))
        {
            // Found a cycle
            return true;
        }

        visited.Add(current);
        path.Add(current.Name);

        if (dependencyMap.TryGetValue(current, out var dependencies))
        {
            foreach (var dependency in dependencies)
            {
                if (HasCircularDependency(dependency, dependencyMap, visited, path))
                {
                    return true;
                }
            }
        }

        visited.Remove(current);
        path.RemoveAt(path.Count - 1);

        return false;
    }

    private static List<INamedTypeSymbol> GetDependencies(INamedTypeSymbol pluginSymbol)
    {
        var dependencies = new List<INamedTypeSymbol>();

        // Get the Dependencies property from the plugin symbol
        var dependenciesProperty = pluginSymbol.GetMembers("Dependencies")
            .OfType<IPropertySymbol>()
            .FirstOrDefault();

        if (dependenciesProperty == null)
            return dependencies;

        // Get the syntax for the property getter to extract typeof() expressions
        foreach (var syntaxRef in dependenciesProperty.DeclaringSyntaxReferences)
        {
            var propertySyntax = syntaxRef.GetSyntax() as PropertyDeclarationSyntax;
            if (propertySyntax == null)
                continue;

            // Extract typeof() expressions from the property
            var typeofExpressions = propertySyntax.DescendantNodes().OfType<TypeOfExpressionSyntax>();

            foreach (var typeofExpr in typeofExpressions)
            {
                // Get type name from the typeof expression syntax
                var typeName = typeofExpr.Type.ToString();
                
                // Find the type in the same assembly as the plugin
                var candidateTypes = GetAllTypesInAssembly(pluginSymbol.ContainingAssembly);
                var matchedType = candidateTypes
                    .FirstOrDefault(t => t.Name == typeName || t.ToDisplayString() == typeName);
                
                if (matchedType != null)
                {
                    dependencies.Add(matchedType);
                }
            }
        }

        return dependencies;
    }

    private static IEnumerable<INamedTypeSymbol> GetAllTypesInAssembly(IAssemblySymbol assembly)
    {
        var stack = new Stack<INamespaceSymbol>();
        stack.Push(assembly.GlobalNamespace);

        while (stack.Count > 0)
        {
            var currentNamespace = stack.Pop();

            foreach (var type in currentNamespace.GetTypeMembers())
            {
                yield return type;
            }

            foreach (var childNamespace in currentNamespace.GetNamespaceMembers())
            {
                stack.Push(childNamespace);
            }
        }
    }

    private static bool ImplementsInterface(INamedTypeSymbol classSymbol, INamedTypeSymbol interfaceSymbol)
    {
        return classSymbol.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, interfaceSymbol));
    }
}
