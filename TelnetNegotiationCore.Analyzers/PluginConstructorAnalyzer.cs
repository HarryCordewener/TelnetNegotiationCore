using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Linq;

namespace TelnetNegotiationCore.Analyzers;

/// <summary>
/// Analyzer that ensures plugin classes have parameterless constructors for builder pattern.
/// Rule TNCP005: Plugin classes must have a parameterless constructor
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class PluginConstructorAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "TNCP005";

    private static readonly LocalizableString Title = "Plugin must have parameterless constructor";
    private static readonly LocalizableString MessageFormat =
        "Plugin '{0}' must have a parameterless constructor for use with AddPlugin<T>() builder method";
    private static readonly LocalizableString Description =
        "Plugin classes used with the builder pattern (AddPlugin<T>()) must have a public parameterless constructor. " +
        "This allows the framework to instantiate the plugin automatically.";

    private const string Category = "TelnetPlugin";

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
        DiagnosticId,
        Title,
        MessageFormat,
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: Description);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSyntaxNodeAction(AnalyzeClass, SyntaxKind.ClassDeclaration);
    }

    private static void AnalyzeClass(SyntaxNodeAnalysisContext context)
    {
        var classDecl = (ClassDeclarationSyntax)context.Node;

        var classSymbol = context.SemanticModel.GetDeclaredSymbol(classDecl) as INamedTypeSymbol;
        if (classSymbol == null)
            return;

        // Check if class implements ITelnetProtocolPlugin
        var pluginInterface = context.Compilation.GetTypeByMetadataName("TelnetNegotiationCore.Plugins.ITelnetProtocolPlugin");
        if (pluginInterface == null)
            return;

        if (!ImplementsInterface(classSymbol, pluginInterface))
            return;

        // Check if class is abstract (abstract classes don't need parameterless constructors)
        if (classSymbol.IsAbstract)
            return;

        // Check if class has a parameterless constructor
        var constructors = classSymbol.Constructors;
        
        // If no explicit constructors, C# provides an implicit parameterless constructor
        if (constructors.Length == 0)
            return;

        // Check if there's an accessible parameterless constructor
        var hasParameterlessConstructor = constructors.Any(c => 
            c.Parameters.Length == 0 && 
            (c.DeclaredAccessibility == Accessibility.Public || c.DeclaredAccessibility == Accessibility.Internal));

        if (!hasParameterlessConstructor)
        {
            var diagnostic = Diagnostic.Create(
                Rule,
                classDecl.Identifier.GetLocation(),
                classSymbol.Name);

            context.ReportDiagnostic(diagnostic);
        }
    }

    private static bool ImplementsInterface(INamedTypeSymbol classSymbol, INamedTypeSymbol interfaceSymbol)
    {
        return classSymbol.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, interfaceSymbol));
    }
}
