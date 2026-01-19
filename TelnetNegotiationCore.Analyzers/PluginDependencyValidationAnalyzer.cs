using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Linq;

namespace TelnetNegotiationCore.Analyzers;

/// <summary>
/// Analyzer that validates plugin dependencies are valid ITelnetProtocolPlugin types.
/// Rule TNCP003: Plugin dependencies must be valid plugin types
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class PluginDependencyValidationAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "TNCP003";

    private static readonly LocalizableString Title = "Invalid plugin dependency type";
    private static readonly LocalizableString MessageFormat =
        "Plugin '{0}' declares dependency on '{1}' which does not implement ITelnetProtocolPlugin";
    private static readonly LocalizableString Description =
        "All types in the Dependencies property must implement ITelnetProtocolPlugin interface.";

    private const string Category = "TelnetPlugin";

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
        DiagnosticId,
        Title,
        MessageFormat,
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: Description);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSyntaxNodeAction(AnalyzeProperty, SyntaxKind.PropertyDeclaration);
    }

    private static void AnalyzeProperty(SyntaxNodeAnalysisContext context)
    {
        var propertyDecl = (PropertyDeclarationSyntax)context.Node;

        // Check if this is the Dependencies property
        if (propertyDecl.Identifier.Text != "Dependencies")
            return;

        // Get the containing class
        var classDecl = propertyDecl.FirstAncestorOrSelf<ClassDeclarationSyntax>();
        if (classDecl == null)
            return;

        var classSymbol = context.SemanticModel.GetDeclaredSymbol(classDecl) as INamedTypeSymbol;
        if (classSymbol == null)
            return;

        // Check if class implements ITelnetProtocolPlugin
        var pluginInterface = context.Compilation.GetTypeByMetadataName("TelnetNegotiationCore.Plugins.ITelnetProtocolPlugin");
        if (pluginInterface == null)
            return;

        if (!ImplementsInterface(classSymbol, pluginInterface))
            return;

        // Analyze all typeof() expressions in the Dependencies property
        var typeofExpressions = propertyDecl.DescendantNodes().OfType<TypeOfExpressionSyntax>();

        foreach (var typeofExpr in typeofExpressions)
        {
            var typeInfo = context.SemanticModel.GetTypeInfo(typeofExpr.Type);
            if (typeInfo.Type is INamedTypeSymbol dependencyType)
            {
                // Check if dependency type implements ITelnetProtocolPlugin
                if (!ImplementsInterface(dependencyType, pluginInterface))
                {
                    var diagnostic = Diagnostic.Create(
                        Rule,
                        typeofExpr.GetLocation(),
                        classSymbol.Name,
                        dependencyType.Name);

                    context.ReportDiagnostic(diagnostic);
                }
            }
        }
    }

    private static bool ImplementsInterface(INamedTypeSymbol classSymbol, INamedTypeSymbol interfaceSymbol)
    {
        return classSymbol.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, interfaceSymbol));
    }
}
