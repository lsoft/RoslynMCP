using Microsoft.Extensions.DependencyInjection;
using RoslynMcpServer.Tests.Infrastructure;
using RoslynMcpServer.Tools;

namespace RoslynMcpServer.Tests
{
    /// <summary>Position-based symbol resolution (go to definition) and type/call hierarchies.</summary>
    public class SymbolNavigationTests : IDisposable
    {
        private const string ShapesCode = """
            namespace Shop.Shapes
            {
                public interface IShape
                {
                    double Area();
                }

                public interface ISolid : IShape { }

                public abstract class Shape : IShape
                {
                    public abstract double Area();
                    public virtual string Describe() => "shape";
                }

                public class Square : Shape
                {
                    public override double Area() => 4;
                    public override string Describe() => "square";
                }

                public class Circle : IShape
                {
                    public double Area() => 3.14;
                }
            }
            """;

        private const string ReportCode = """
            using System;
            using Shop.Shapes;

            namespace Shop.Reports
            {
                public static class Report
                {
                    public static double Total(IShape shape) => shape.Area();

                    public static double CircleArea(Circle circle) => circle.Area();

                    public static void Print() => Console.WriteLine(Total(new Square()));
                }
            }
            """;

        private readonly TestSolutionBuilder _builder = new();
        private readonly ServiceProvider _services;

        public SymbolNavigationTests()
        {
            var solution = _builder
                .AddProject("Core", ("Shapes.cs", ShapesCode), ("Report.cs", ReportCode))
                .AddProject("Legacy", ("Report.cs", "class LegacyReport { }"))
                .WriteFiles()
                .Build();
            _services = TestServices.Create(solution);
        }

        public void Dispose()
        {
            _services.Dispose();
            _builder.Dispose();
        }

        private string Path => _builder.SolutionPath;

        private Task<string> Info(string? name = null, string? file = null, int? line = null, int? column = null) =>
            CodeNavigationTools.GetSymbolInfo(Path, name, file, line, column, serviceProvider: _services);

        private Task<string> Related(string relation, string? name = null, string? file = null, int? line = null) =>
            CodeNavigationTools.FindReferences(Path, name, file, line, relation: relation, serviceProvider: _services);

        [Fact]
        public async Task ByName_ListsOtherMatches()
        {
            // Regression: an ambiguous name silently picked the first symbol
            var output = await Info("Area");

            Assert.Contains("Also matches (3, pass filePath + line to pick one):", output);
        }

        [Fact]
        public async Task ByPosition_ResolvesTheExactSymbol()
        {
            var output = await Info("Area", "Core/Shapes.cs", 24);

            Assert.Contains("Full Name: `Shop.Shapes.Circle.Area()`", output);
            Assert.DoesNotContain("Also matches", output);
        }

        [Fact]
        public async Task ByColumn_ResolvesTheTokenUnderTheCursor()
        {
            var output = await Info(file: "Core/Shapes.cs", line: 22, column: 18);

            Assert.Contains("Full Name: `Shop.Shapes.Circle`", output);
            Assert.Contains("Location: Core/Shapes.cs:22", output);
        }

        [Fact]
        public async Task GoToDefinition_FromAUsage()
        {
            var output = await Info("Area", "Core/Report.cs", 10);

            Assert.Contains("Full Name: `Shop.Shapes.Circle.Area()`", output);
            Assert.Contains("Location: Core/Shapes.cs:24", output);
        }

        [Fact]
        public async Task GoToDefinition_IntoAReferencedAssembly()
        {
            var output = await Info("WriteLine", "Core/Report.cs", 12);

            Assert.Contains("System.Console.WriteLine(", output);
            Assert.Contains("Location: metadata System.Console", output);
        }

        [Fact]
        public async Task AbsolutePathsWork()
        {
            var output = await Info("Area", System.IO.Path.Combine(_builder.Root, "Core", "Shapes.cs"), 24);

            Assert.Contains("Shop.Shapes.Circle.Area()", output);
        }

        [Theory]
        [InlineData("Core/Shapes.cs", 99, "Area", "Error: Line 99 is outside Core/Shapes.cs")]
        [InlineData("Core/Shapes.cs", 1, "Area", "Error: 'Area' does not occur on line 1.")]
        [InlineData("Nope.cs", 1, "Area", "Error: 'Nope.cs' is not part of the solution.")]
        [InlineData("Report.cs", 1, "Area", "Error: 'Report.cs' is ambiguous, candidates:")]
        [InlineData("Core/Shapes.cs", null, "Area", "Error: line is required together with filePath.")]
        [InlineData(null, null, null, "Error: Provide symbolName")]
        public async Task ReportsUnusableQueries(string? file, int? line, string? name, string expected)
        {
            Assert.StartsWith(expected, await Info(name, file, line));
        }

        [Fact]
        public async Task Implementations_OfAnInterface()
        {
            var output = await Related("implementations", "IShape", "Core/Shapes.cs", 3);

            Assert.StartsWith("Found 4 implementations of `Shop.Shapes.IShape`:", output);
            foreach (var type in new[] { "Shape", "Square", "Circle", "ISolid" })
                Assert.Contains($"`Shop.Shapes.{type}`", output);
        }

        [Fact]
        public async Task Implementations_OfAnInterfaceMember()
        {
            var output = await Related("implementations", "Area", "Core/Shapes.cs", 5);

            Assert.Contains("`Shop.Shapes.Circle.Area()` at Core/Shapes.cs:24", output);
            Assert.Contains("`Shop.Shapes.Shape.Area()` at Core/Shapes.cs:12", output);
        }

        [Fact]
        public async Task Implementations_OfAVirtualMemberAreItsOverrides()
        {
            var output = await Related("implementations", "Describe", "Core/Shapes.cs", 13);

            Assert.Contains("`Shop.Shapes.Square.Describe()` at Core/Shapes.cs:19", output);
        }

        [Fact]
        public async Task Base_OfAType()
        {
            var output = await Related("base", "Square", "Core/Shapes.cs", 16);

            Assert.StartsWith("Found 2 base types or members of `Shop.Shapes.Square`:", output);
            Assert.Contains("`Shop.Shapes.Shape`", output);
            Assert.Contains("`Shop.Shapes.IShape`", output);
        }

        [Fact]
        public async Task Base_OfAnOverride()
        {
            var output = await Related("base", "Area", "Core/Shapes.cs", 18);

            Assert.Contains("`Shop.Shapes.Shape.Area()`", output);
            Assert.Contains("`Shop.Shapes.IShape.Area()`", output);
        }

        [Fact]
        public async Task Callers_ListCallSites()
        {
            var output = await Related("callers", "Area", "Core/Shapes.cs", 24);

            Assert.Contains("`Shop.Reports.Report.CircleArea(Shop.Shapes.Circle)` at Core/Report.cs:10", output);
            Assert.Contains("L10: public static double CircleArea(Circle circle) => circle.Area();", output);
        }

        [Fact]
        public async Task Callers_OfAnInterfaceMember()
        {
            var output = await Related("callers", "Area", "Core/Shapes.cs", 5);

            Assert.Contains("`Shop.Reports.Report.Total(Shop.Shapes.IShape)`", output);
        }

        [Fact]
        public async Task References_ByPosition()
        {
            var output = await Related("references", "Area", "Core/Shapes.cs", 24);

            // Like Find All References in the IDE, calls through the implemented interface member count:
            // shape.Area() may dispatch to Circle.Area. Square's and Shape's Area are other symbols.
            Assert.DoesNotContain("Matched", output);
            Assert.StartsWith("Found 3 references:", output);
            Assert.Contains("circle.Area()", output);
            Assert.Contains("shape.Area()", output);
            Assert.DoesNotContain("override double Area", output);
        }

        [Fact]
        public async Task References_ByAmbiguousName_SaysWhatMatched()
        {
            var output = await Related("references", "Area");

            Assert.StartsWith("Matched 4 symbols:", output);
        }

        [Fact]
        public async Task UnknownRelation()
        {
            Assert.StartsWith("Error: Unknown relation 'parents'", await Related("parents", "Area"));
        }
    }
}
