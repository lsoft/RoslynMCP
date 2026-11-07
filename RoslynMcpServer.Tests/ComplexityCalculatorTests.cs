using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcpServer.Services;

namespace RoslynMcpServer.Tests
{
    public class ComplexityCalculatorTests
    {
        private static int ComplexityOf(string methodBody, string parameters = "int x, int? y, object o")
        {
            var code = $"class C {{ int M({parameters}) {{ {methodBody} }} }}";
            var method = CSharpSyntaxTree.ParseText(code).GetRoot()
                .DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
            return ComplexityCalculator.CalculateCyclomaticComplexity(method);
        }

        [Fact]
        public void StraightLineCode_IsOne()
        {
            Assert.Equal(1, ComplexityOf("var a = x + 1; return a;"));
        }

        [Theory]
        [InlineData("if (x > 0) return 1; return 0;", 2)]
        [InlineData("if (x > 0) return 1; else if (x < 0) return -1; return 0;", 3)]
        [InlineData("while (x > 0) x--; return x;", 2)]
        [InlineData("do { x--; } while (x > 0); return x;", 2)]
        [InlineData("for (var i = 0; i < x; i++) { } return x;", 2)]
        [InlineData("foreach (var c in \"ab\") { } return x;", 2)]
        [InlineData("foreach (var (a, b) in new[] { (1, 2) }) { } return x;", 2)]
        [InlineData("try { return 1; } catch (System.ArgumentException) { return 2; } catch { return 3; }", 3)]
        public void Statements(string body, int expected)
        {
            Assert.Equal(expected, ComplexityOf(body));
        }

        [Theory]
        [InlineData("return x > 0 && x < 10 ? 1 : 0;", 3)]
        [InlineData("return x > 0 || x < -10 ? 1 : 0;", 3)]
        [InlineData("return y ?? 0;", 2)]
        [InlineData("y ??= 0; return y.Value;", 2)]
        public void Expressions(string body, int expected)
        {
            Assert.Equal(expected, ComplexityOf(body));
        }

        [Fact]
        public void SwitchStatement_CountsEveryCaseButNotDefault()
        {
            // Regression: the whole switch used to count as a single decision point
            var body = "switch (x) { case 1: return 1; case 2: case 3: return 2; case int n when n > 10: return 3; default: return 0; }";
            Assert.Equal(5, ComplexityOf(body));
        }

        [Fact]
        public void SwitchExpression_CountsArmsExceptDiscard()
        {
            Assert.Equal(3, ComplexityOf("return x switch { 1 => 10, 2 => 20, _ => 0 };"));
        }

        [Fact]
        public void NestedLambdasCountTowardsTheMethod()
        {
            Assert.Equal(2, ComplexityOf("System.Func<int, int> f = a => a > 0 ? a : -a; return f(x);"));
        }

        [Fact]
        public void Analyze_ReportsOnlyMethodsAtOrAboveThreshold()
        {
            var code = """
                namespace Shop
                {
                    class Orders
                    {
                        void Simple() { }
                        int Complex(int x)
                        {
                            if (x > 0 && x < 10) return 1;
                            if (x > 100 || x < -100) return 2;
                            return 0;
                        }
                    }
                }
                """;
            var root = CSharpSyntaxTree.ParseText(code).GetRoot();

            var results = ComplexityCalculator.Analyze(root, Path.Combine("src", "Orders.cs"), threshold: 5);

            var result = Assert.Single(results);
            Assert.Equal("Complex", result.MethodName);
            Assert.Equal(5, result.Complexity);
            Assert.Equal("Orders.cs", result.FileName);
            Assert.Equal(6, result.LineNumber);
            Assert.Equal("Orders", result.ClassName);
            Assert.Equal("Shop", result.Namespace);
        }

        [Fact]
        public void Analyze_ThresholdOfOneReportsEverything()
        {
            var root = CSharpSyntaxTree.ParseText("class A { void M1() { } void M2() { } }").GetRoot();
            Assert.Equal(2, ComplexityCalculator.Analyze(root, "A.cs", threshold: 1).Count);
        }

        [Fact]
        public void FileScopedNamespace_IsReported()
        {
            // Regression: only block-scoped namespaces were recognized
            var root = CSharpSyntaxTree.ParseText("namespace Shop.Orders; class A { void M() { } }").GetRoot();
            Assert.Equal("Shop.Orders", ComplexityCalculator.GetContainingNamespace(FirstMethod(root)));
        }

        [Fact]
        public void NestedNamespaces_AreJoined()
        {
            var root = CSharpSyntaxTree.ParseText("namespace A { namespace B { class C { void M() { } } } }").GetRoot();
            Assert.Equal("A.B", ComplexityCalculator.GetContainingNamespace(FirstMethod(root)));
        }

        [Fact]
        public void GlobalNamespace_IsEmpty()
        {
            var root = CSharpSyntaxTree.ParseText("class C { void M() { } }").GetRoot();
            Assert.Equal("", ComplexityCalculator.GetContainingNamespace(FirstMethod(root)));
        }

        [Theory]
        [InlineData("struct S { void M() { } }", "S")]
        [InlineData("record R { void M() { } }", "R")]
        [InlineData("record struct RS { void M() { } }", "RS")]
        [InlineData("interface I { void M() { } }", "I")]
        [InlineData("class Outer { class Inner { void M() { } } }", "Outer.Inner")]
        public void ContainingTypeName_CoversAllTypeKinds(string code, string expected)
        {
            // Regression: only classes were recognized, other containers produced ".M"
            var root = CSharpSyntaxTree.ParseText(code).GetRoot();
            Assert.Equal(expected, ComplexityCalculator.GetContainingTypeName(FirstMethod(root)));
        }

        private static MethodDeclarationSyntax FirstMethod(Microsoft.CodeAnalysis.SyntaxNode root)
        {
            return root.DescendantNodes().OfType<MethodDeclarationSyntax>().First();
        }
    }
}
