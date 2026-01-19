using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Linq;

namespace TelnetNegotiationCore.Analyzers;

/// <summary>
/// Analyzer that ensures ProtocolType property returns the correct type for plugin classes.
/// Rule TNCP001: ProtocolType must return typeof(DeclaringClass)
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class PluginProtocolTypeAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "TNCP001";

    private static readonly LocalizableString Title = "ProtocolType must return declaring type";
    private static readonly LocalizableString MessageFormat = 
        "Plugin '{0}' ProtocolType returns 'typeof({1})' but should return 'typeof({0})'";
    private static readonly LocalizableString Description = 
        "The ProtocolType property must return typeof() of the declaring class for proper plugin registration.";
    
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
        
        // Register for property declarations
        context.RegisterSyntaxNodeAction(AnalyzePropertyDeclaration, SyntaxKind.PropertyDeclaration);
    }

    private static void AnalyzePropertyDeclaration(SyntaxNodeAnalysisContext context)
    {
        var propertyDeclaration = (PropertyDeclarationSyntax)context.Node;

        // Check if this is the ProtocolType property
        if (propertyDeclaration.Identifier.Text != "ProtocolType")
            return;

        // Get the containing class
        var classDeclaration = propertyDeclaration.FirstAncestorOrSelf<ClassDeclarationSyntax>();
        if (classDeclaration == null)
            return;

        var classSymbol = context.SemanticModel.GetDeclaredSymbol(classDeclaration);
        if (classSymbol == null)
            return;

        // Check if this class implements ITelnetProtocolPlugin
        if (!ImplementsInterface(classSymbol, "ITelnetProtocolPlugin"))
            return;

        // Get the property symbol
        var propertySymbol = context.SemanticModel.GetDeclaredSymbol(propertyDeclaration);
        if (propertySymbol == null)
            return;

        // Check if ProtocolType returns System.Type
        if (propertySymbol.Type.ToDisplayString() != "System.Type")
            return;

        // Analyze the property expression
        ArrowExpressionClauseSyntax? arrowExpression = propertyDeclaration.ExpressionBody;
        ExpressionSyntax? returnExpression = null;

        if (arrowExpression != null)
        {
            // Expression-bodied property: => typeof(...)
            returnExpression = arrowExpression.Expression;
        }
        else if (propertyDeclaration.AccessorList != null)
        {
            // Get accessor with body
            var getter = propertyDeclaration.AccessorList.Accessors
                .FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
                
            if (getter?.ExpressionBody != null)
            {
                // Expression-bodied accessor: get => typeof(...)
                returnExpression = getter.ExpressionBody.Expression;
            }
            else if (getter?.Body != null)
            {
                // Regular body: get { return typeof(...); }
                var returnStatement = getter.Body.Statements
                    .OfType<ReturnStatementSyntax>()
                    .FirstOrDefault();
                returnExpression = returnStatement?.Expression;
            }
        }

        if (returnExpression == null)
            return;

        // Check if it's a typeof expression
        if (returnExpression is not TypeOfExpressionSyntax typeOfExpression)
            return;

        // Get the type argument
        var typeArgument = context.SemanticModel.GetSymbolInfo(typeOfExpression.Type).Symbol as INamedTypeSymbol;
        if (typeArgument == null)
            return;

        // Compare with declaring class
        if (!SymbolEqualityComparer.Default.Equals(typeArgument, classSymbol))
        {
            var diagnostic = Diagnostic.Create(
                Rule,
                typeOfExpression.GetLocation(),
                classSymbol.Name,
                typeArgument.Name);

            context.ReportDiagnostic(diagnostic);
        }
    }

    private static bool ImplementsInterface(INamedTypeSymbol classSymbol, string interfaceName)
    {
        return classSymbol.AllInterfaces.Any(i => i.Name == interfaceName);
    }
}
