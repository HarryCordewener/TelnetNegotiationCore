using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Linq;

namespace TelnetNegotiationCore.Analyzers;

/// <summary>
/// Analyzer that detects potentially incomplete ConfigureStateMachine implementations.
/// Rule TNCP004: ConfigureStateMachine should configure state transitions, not just log
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class ConfigureStateMachineAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "TNCP004";

    private static readonly LocalizableString Title = "ConfigureStateMachine appears to be empty or incomplete";
    private static readonly LocalizableString MessageFormat =
        "Plugin '{0}' ConfigureStateMachine method only contains logging or is empty - state machine transitions should be configured here";
    private static readonly LocalizableString Description =
        "The ConfigureStateMachine method should configure state machine transitions for the protocol. " +
        "An implementation that only logs or is empty suggests the plugin integration is incomplete.";

    private const string Category = "TelnetPlugin";

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
        DiagnosticId,
        Title,
        MessageFormat,
        Category,
        DiagnosticSeverity.Info,  // Info level - highlights incomplete migrations without breaking builds
        isEnabledByDefault: true,
        description: Description);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        // Register for method declarations
        context.RegisterSyntaxNodeAction(AnalyzeMethodDeclaration, SyntaxKind.MethodDeclaration);
    }

    private static void AnalyzeMethodDeclaration(SyntaxNodeAnalysisContext context)
    {
        var methodDeclaration = (MethodDeclarationSyntax)context.Node;

        // Check if this is the ConfigureStateMachine method
        if (methodDeclaration.Identifier.Text != "ConfigureStateMachine")
            return;

        // Get the containing class
        var classDeclaration = methodDeclaration.FirstAncestorOrSelf<ClassDeclarationSyntax>();
        if (classDeclaration == null)
            return;

        var classSymbol = context.SemanticModel.GetDeclaredSymbol(classDeclaration);
        if (classSymbol == null)
            return;

        // Check if this class implements ITelnetProtocolPlugin
        if (!ImplementsInterface(classSymbol, "ITelnetProtocolPlugin"))
            return;

        // Analyze the method body
        var body = methodDeclaration.Body;
        if (body == null)
            return;

        // Check if method is empty or only contains logging
        if (IsEmptyOrOnlyLogging(body))
        {
            var diagnostic = Diagnostic.Create(
                Rule,
                methodDeclaration.Identifier.GetLocation(),
                classSymbol.Name);

            context.ReportDiagnostic(diagnostic);
        }
    }

    private static bool IsEmptyOrOnlyLogging(BlockSyntax body)
    {
        var statements = body.Statements;

        // Empty method
        if (statements.Count == 0)
            return true;

        // Check if all statements are just logging calls
        foreach (var statement in statements)
        {
            if (!IsLoggingStatement(statement))
            {
                // Found a non-logging statement, so this is not empty/logging-only
                return false;
            }
        }

        // All statements are logging
        return true;
    }

    private static bool IsLoggingStatement(StatementSyntax statement)
    {
        // Check for expression statements that are logging calls
        if (statement is ExpressionStatementSyntax expressionStatement)
        {
            var expression = expressionStatement.Expression;

            // Check for invocation expressions
            if (expression is InvocationExpressionSyntax invocation)
            {
                // Check if it's a member access (e.g., context.Logger.LogInformation)
                if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
                {
                    var memberName = memberAccess.Name.Identifier.Text;

                    // Check if it's a logging method
                    if (memberName.StartsWith("Log") || memberName == "WriteLine" || memberName == "Write")
                    {
                        return true;
                    }

                    // Check for Logger property access
                    if (memberAccess.Expression is MemberAccessExpressionSyntax innerMemberAccess)
                    {
                        if (innerMemberAccess.Name.Identifier.Text == "Logger")
                        {
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    private static bool ImplementsInterface(INamedTypeSymbol classSymbol, string interfaceName)
    {
        return classSymbol.AllInterfaces.Any(i => i.Name == interfaceName);
    }
}
