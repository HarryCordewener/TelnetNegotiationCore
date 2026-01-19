using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Linq;

namespace TelnetNegotiationCore.Analyzers
{
    /// <summary>
    /// Analyzer TNCP006: Validates that plugins with [RequiredMethod] attributes
    /// have those methods called before usage.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class PluginRequiredMethodAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "TNCP006";

        private static readonly LocalizableString Title = 
            "Plugin requires method calls before initialization";
        
        private static readonly LocalizableString MessageFormat = 
            "Plugin '{0}' requires calls to the following methods before InitializeAsync: {1}";
        
        private static readonly LocalizableString Description = 
            "Plugins decorated with [RequiredMethod] attributes must document which methods " +
            "need to be called before the plugin is initialized. This serves as executable " +
            "documentation for plugin consumers.";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            DiagnosticId,
            Title,
            MessageFormat,
            "TelnetNegotiationCore.PluginArchitecture",
            DiagnosticSeverity.Info,
            isEnabledByDefault: true,
            description: Description);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => 
            ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            // Analyze class declarations to find plugins with [RequiredMethod] attributes
            context.RegisterSyntaxNodeAction(AnalyzeClassDeclaration, SyntaxKind.ClassDeclaration);
        }

        private void AnalyzeClassDeclaration(SyntaxNodeAnalysisContext context)
        {
            var classDeclaration = (ClassDeclarationSyntax)context.Node;
            var classSymbol = context.SemanticModel.GetDeclaredSymbol(classDeclaration);

            if (classSymbol == null || classSymbol.IsAbstract)
                return;

            // Check if class implements ITelnetProtocolPlugin
            var pluginInterface = context.Compilation.GetTypeByMetadataName(
                "TelnetNegotiationCore.Plugins.ITelnetProtocolPlugin");

            if (pluginInterface == null)
                return;

            if (!classSymbol.AllInterfaces.Contains(pluginInterface, SymbolEqualityComparer.Default))
                return;

            // Find all [RequiredMethod] attributes on the class
            var requiredMethods = classSymbol.GetAttributes()
                .Where(attr => 
                {
                    if (attr.AttributeClass == null)
                        return false;
                    
                    // Match both name and namespace to avoid false positives
                    return attr.AttributeClass.Name == "RequiredMethodAttribute" &&
                           attr.AttributeClass.ContainingNamespace?.ToString() == "TelnetNegotiationCore.Attributes";
                })
                .Select(attr => attr.ConstructorArguments.FirstOrDefault().Value?.ToString())
                .Where(name => !string.IsNullOrEmpty(name))
                .ToList();

            if (requiredMethods.Count == 0)
                return;

            // Report info diagnostic documenting the required methods
            var methodList = string.Join(", ", requiredMethods);
            var diagnostic = Diagnostic.Create(
                Rule,
                classDeclaration.Identifier.GetLocation(),
                classSymbol.Name,
                methodList);

            context.ReportDiagnostic(diagnostic);
        }
    }
}
