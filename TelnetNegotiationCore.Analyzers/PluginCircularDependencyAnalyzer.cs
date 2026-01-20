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

        context.RegisterCompilationAction(AnalyzeCompilation);
    }

    private static void AnalyzeCompilation(CompilationAnalysisContext context)
    {
        // Find all classes that implement ITelnetProtocolPlugin
        var pluginInterface = context.Compilation.GetTypeByMetadataName("TelnetNegotiationCore.Plugins.ITelnetProtocolPlugin");
        if (pluginInterface == null)
            return;

        var plugins = new List<(INamedTypeSymbol Symbol, SyntaxNode Node, SemanticModel Model)>();

        foreach (var syntaxTree in context.Compilation.SyntaxTrees)
        {
#pragma warning disable RS1030 // Do not invoke Compilation.GetSemanticModel() method within a diagnostic analyzer
            var semanticModel = context.Compilation.GetSemanticModel(syntaxTree);
#pragma warning restore RS1030 // Do not invoke Compilation.GetSemanticModel() method within a diagnostic analyzer
            var root = syntaxTree.GetRoot(context.CancellationToken);

            var classDeclarations = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

            foreach (var classDecl in classDeclarations)
            {
                var classSymbol = semanticModel.GetDeclaredSymbol(classDecl, context.CancellationToken) as INamedTypeSymbol;
                if (classSymbol == null)
                    continue;

                if (ImplementsInterface(classSymbol, pluginInterface))
                {
                    plugins.Add((classSymbol, classDecl, semanticModel));
                }
            }
        }

        // Build dependency graph
        var dependencyMap = new Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>>(SymbolEqualityComparer.Default);

        foreach (var (symbol, node, model) in plugins)
        {
            var dependencies = GetDependencies(symbol, node, model);
            dependencyMap[symbol] = dependencies;
        }

        // Detect circular dependencies
        foreach (var (symbol, node, _) in plugins)
        {
            var visited = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            var path = new List<string>();

            if (HasCircularDependency(symbol, dependencyMap, visited, path))
            {
                var cycle = string.Join(" → ", path) + " → " + symbol.Name;
                var diagnostic = Diagnostic.Create(
                    Rule,
                    node.GetLocation(),
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

    private static List<INamedTypeSymbol> GetDependencies(INamedTypeSymbol pluginSymbol, SyntaxNode node, SemanticModel semanticModel)
    {
        var dependencies = new List<INamedTypeSymbol>();

        if (node is not ClassDeclarationSyntax classDecl)
            return dependencies;

        var propertyDecl = classDecl.Members
            .OfType<PropertyDeclarationSyntax>()
            .FirstOrDefault(p => p.Identifier.Text == "Dependencies");

        if (propertyDecl == null)
            return dependencies;

        // Extract typeof() expressions from the property
        var typeofExpressions = propertyDecl.DescendantNodes().OfType<TypeOfExpressionSyntax>();

        foreach (var typeofExpr in typeofExpressions)
        {
            var typeInfo = semanticModel.GetTypeInfo(typeofExpr.Type);
            if (typeInfo.Type is INamedTypeSymbol namedType)
            {
                dependencies.Add(namedType);
            }
        }

        return dependencies;
    }

    private static bool ImplementsInterface(INamedTypeSymbol classSymbol, INamedTypeSymbol interfaceSymbol)
    {
        return classSymbol.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, interfaceSymbol));
    }
}
