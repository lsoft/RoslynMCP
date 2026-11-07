using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcpServer.Models;

namespace RoslynMcpServer.Services
{
    public static class ComplexityCalculator
    {
        public static List<ComplexityResult> Analyze(SyntaxNode root, string filePath, int threshold)
        {
            var complexityResults = new List<ComplexityResult>();
            var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>();

            foreach (var method in methods)
            {
                var complexity = CalculateCyclomaticComplexity(method);
                if (complexity >= threshold)
                {
                    var lineSpan = method.GetLocation().GetLineSpan();
                    complexityResults.Add(new ComplexityResult
                    {
                        MethodName = method.Identifier.ValueText,
                        FileName = Path.GetFileName(filePath),
                        LineNumber = lineSpan.StartLinePosition.Line + 1,
                        Complexity = complexity,
                        ClassName = GetContainingTypeName(method),
                        Namespace = GetContainingNamespace(method)
                    });
                }
            }

            return complexityResults;
        }

        // McCabe complexity: 1 + number of decision points
        public static int CalculateCyclomaticComplexity(SyntaxNode method)
        {
            int complexity = 1; // Base complexity

            foreach (var node in method.DescendantNodes())
            {
                switch (node.Kind())
                {
                    case SyntaxKind.IfStatement:
                    case SyntaxKind.WhileStatement:
                    case SyntaxKind.DoStatement:
                    case SyntaxKind.ForStatement:
                    case SyntaxKind.ForEachStatement:
                    case SyntaxKind.ForEachVariableStatement:
                    case SyntaxKind.CaseSwitchLabel:
                    case SyntaxKind.CasePatternSwitchLabel:
                    case SyntaxKind.CatchClause:
                    case SyntaxKind.ConditionalExpression:
                    case SyntaxKind.CoalesceExpression:
                    case SyntaxKind.CoalesceAssignmentExpression:
                    case SyntaxKind.LogicalAndExpression:
                    case SyntaxKind.LogicalOrExpression:
                        complexity++;
                        break;

                    // The discard arm is the switch expression's "default", it adds no branch
                    case SyntaxKind.SwitchExpressionArm when ((SwitchExpressionArmSyntax)node).Pattern is not DiscardPatternSyntax:
                        complexity++;
                        break;
                }
            }

            return complexity;
        }

        public static string GetContainingTypeName(SyntaxNode node)
        {
            var types = node.Ancestors()
                .OfType<BaseTypeDeclarationSyntax>()
                .Select(t => t.Identifier.ValueText)
                .Reverse();
            return string.Join(".", types);
        }

        public static string GetContainingNamespace(SyntaxNode node)
        {
            var namespaces = node.Ancestors()
                .OfType<BaseNamespaceDeclarationSyntax>()
                .Select(n => n.Name.ToString())
                .Reverse();
            return string.Join(".", namespaces);
        }
    }
}
