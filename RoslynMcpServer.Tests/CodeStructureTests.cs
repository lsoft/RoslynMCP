using Microsoft.Extensions.DependencyInjection;
using RoslynMcpServer.Tests.Infrastructure;
using RoslynMcpServer.Tools;

namespace RoslynMcpServer.Tests
{
    public class CodeStructureTests : IDisposable
    {
        private const string OrdersCode = """
            using System;

            namespace Shop.Orders;

            /// <summary>An order. Has lines.</summary>
            public sealed class Order : IComparable<Order>
            {
                private readonly int _id;
                public const int MaxLines = 10;

                public Order(int id) { _id = id; }

                /// <summary>Total amount in cents.</summary>
                public decimal Total { get; private set; }

                public event EventHandler? Changed;

                public int CompareTo(Order? other) => _id.CompareTo(other?._id ?? 0);

                private void Recalculate() { }

                public enum Status { New = 1, Paid = 2 }

                public record Line(string Sku, int Quantity);
            }

            public delegate void OrderHandler(Order order);

            public static partial class Registry
            {
                public static void Add(Order order) { }
            }
            """;

        private const string RegistryExtraCode = """
            namespace Shop.Orders;

            public static partial class Registry
            {
                public static int Count { get; } = 0;
            }
            """;

        private readonly TestSolutionBuilder _builder = new();
        private readonly ServiceProvider _services;

        public CodeStructureTests()
        {
            var solution = _builder
                .AddProject("Core",
                    ("Orders.cs", OrdersCode),
                    ("Registry.Extra.cs", RegistryExtraCode),
                    ("Legacy.cs", "namespace Shop.Legacy; public class Order { }"),
                    ("UseGenerated.cs", "public class UseIt { public string M() => Gen.Hello.World(); }"))
                .AddAnalyzerReference("Core", TestAssemblyAnalyzers.Create())
                .WriteFiles()
                .Build();
            _services = TestServices.Create(solution);
        }

        public void Dispose()
        {
            _services.Dispose();
            _builder.Dispose();
        }

        private Task<string> Outline(string? file = null, string? type = null, bool includePrivate = true, bool includeDocs = true) =>
            CodeStructureTools.GetOutline(_builder.SolutionPath, file, type, includePrivate, includeDocs, serviceProvider: _services);

        [Fact]
        public async Task FileOutline_ShowsSignaturesAndLines()
        {
            var output = await Outline("Core/Orders.cs");

            var expected = new[]
            {
                "Core/Orders.cs (Core, 32 lines)",
                "namespace Shop.Orders",
                "  public sealed class Order : IComparable<Order>  L6-25  // An order.",
                "    private readonly int _id  L8",
                "    public const int MaxLines = 10  L9",
                "    public Order(int id)  L11",
                "    public decimal Total { get; private set; }  L14  // Total amount in cents.",
                "    public event EventHandler? Changed  L16",
                "    public int CompareTo(Order? other)  L18",
                "    private void Recalculate()  L20",
                "    public enum Status  L22-22",
                "      New = 1  L22",
                "      Paid = 2  L22",
                "    public record Line  L24-24",
                "  public delegate void OrderHandler(Order order)  L27-27",
                "  public static class Registry  L29-32",
                "    public static void Add(Order order)  L31",
            };
            foreach (var line in expected)
                Assert.Contains(line + "\n", output);
        }

        [Fact]
        public async Task FileOutline_IsMuchSmallerThanTheFile()
        {
            var output = await Outline("Core/Orders.cs", includeDocs: false);

            Assert.DoesNotContain("=>", output);
            Assert.DoesNotContain("{ _id = id; }", output);
        }

        [Fact]
        public async Task FileOutline_CanHidePrivateMembersAndDocs()
        {
            var output = await Outline("Core/Orders.cs", includePrivate: false, includeDocs: false);

            Assert.DoesNotContain("_id", output);
            Assert.DoesNotContain("Recalculate", output);
            Assert.DoesNotContain("//", output);
            Assert.Contains("public int CompareTo(Order? other)", output);
        }

        [Fact]
        public async Task TypeOutline_SpansPartialDeclarations()
        {
            var output = await Outline(type: "Registry");

            Assert.Contains("public static class Registry  // Core/Orders.cs:29, Core/Registry.Extra.cs:3", output);
            Assert.Contains("public static void Add(Order order)  Core/Orders.cs:31", output);
            Assert.Contains("public static int Count { get; }  Core/Registry.Extra.cs:5", output);
        }

        [Fact]
        public async Task TypeOutline_AmbiguousNameListsCandidates()
        {
            var ambiguous = await Outline(type: "Order");
            var qualified = await Outline(type: "Shop.Orders.Order");

            Assert.StartsWith("'Order' matches 2 types", ambiguous);
            Assert.StartsWith("public sealed class Order : IComparable<Order>", qualified);
        }

        [Fact]
        public async Task Outline_NeedsAFileOrAType()
        {
            Assert.Equal("Error: Provide filePath or typeName.", await Outline());
            Assert.StartsWith("Error: Type 'Nope' not found", await Outline(type: "Nope"));
        }

        [Fact]
        public async Task GeneratedCode_IsListed()
        {
            var output = await CodeStructureTools.GetGeneratedCode(_builder.SolutionPath, serviceProvider: _services);

            Assert.StartsWith("1 generated files:", output);
            Assert.Contains("**Core**:", output);
            Assert.Contains("Hello.g.cs (8 lines)", output);
        }

        [Fact]
        public async Task GeneratedCode_IsReadableWithLineNumbers()
        {
            var output = await CodeStructureTools.GetGeneratedCode(_builder.SolutionPath, "Hello.g.cs", serviceProvider: _services);

            Assert.Contains("(Core, lines 1-8 of 8)", output);
            Assert.Contains("    1: namespace Gen", output);
            Assert.Contains("    6:         public static string World() => \"hi\";", output);
        }

        [Fact]
        public async Task GeneratedCode_IsPaged()
        {
            var output = await CodeStructureTools.GetGeneratedCode(_builder.SolutionPath, "Hello.g.cs", startLine: 3, maxLines: 2, serviceProvider: _services);

            Assert.Contains("lines 3-4 of 8", output);
            Assert.Contains("... 4 more lines (startLine=5).", output);
        }

        [Fact]
        public async Task GeneratedCode_UnknownFile()
        {
            var output = await CodeStructureTools.GetGeneratedCode(_builder.SolutionPath, "Missing.g.cs", serviceProvider: _services);
            Assert.StartsWith("Error: No generated file 'Missing.g.cs'", output);
        }

        [Fact]
        public async Task GeneratedCode_CanBeOutlined()
        {
            var output = await Outline("Hello.g.cs");

            Assert.StartsWith("[generated] Hello.g.cs (Core, 8 lines)", output);
            Assert.Contains("public static string World()  L6  // Says hi.", output);
        }

        [Fact]
        public async Task GeneratedSymbols_PointIntoGeneratedCode()
        {
            var info = await CodeNavigationTools.GetSymbolInfo(_builder.SolutionPath, "World", serviceProvider: _services);
            var references = await CodeNavigationTools.FindReferences(_builder.SolutionPath, "World", serviceProvider: _services);

            Assert.Contains("Location: [generated] Hello.g.cs:6", info);
            Assert.Contains("Summary: Says hi.", info);
            Assert.StartsWith("Found 2 references:", references);
            Assert.Contains("Definition", references);
            Assert.Contains("Gen.Hello.World()", references);
        }
    }
}
