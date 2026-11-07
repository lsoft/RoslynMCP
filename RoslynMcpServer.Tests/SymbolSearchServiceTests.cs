using Microsoft.CodeAnalysis;
using RoslynMcpServer.Services;
using RoslynMcpServer.Tests.Infrastructure;

namespace RoslynMcpServer.Tests
{
    public class SymbolSearchServiceTests : IDisposable
    {
        private const string CustomerCode = """
            namespace Shop.Core
            {
                public class Customer
                {
                    private string name = "";
                    public string Name { get => name; set => name = value; }
                    public int Age { get; set; }
                    internal void Rename(string newName) { name = newName; }

                    public class Address
                    {
                        public string FormatAddress() => "";
                    }
                }

                public interface ICustomerService
                {
                    Customer Find(string name);
                }
            }
            """;

        private const string ServiceCode = """
            namespace Shop.Core
            {
                public class CustomerService : ICustomerService
                {
                    public Customer Find(string name)
                    {
                        var customer = new Customer();
                        customer.Rename(name);
                        return customer;
                    }
                }
            }
            """;

        private const string AppCode = """
            using Shop.Core;

            namespace Shop.App
            {
                public static class Program
                {
                    public static string Run()
                    {
                        var customer = new CustomerService().Find("x");
                        return customer.Name;
                    }
                }
            }
            """;

        private readonly TestSolutionBuilder _builder = new();
        private readonly SymbolSearchService _service;

        public SymbolSearchServiceTests()
        {
            var solution = _builder
                .AddProject("Core", ("Customer.cs", CustomerCode), ("CustomerService.cs", ServiceCode))
                .AddProject("App", new[] { "Core" }, ("Program.cs", AppCode))
                .Build();
            _service = TestServices.CreateSearchService(solution);
        }

        public void Dispose() => _builder.Dispose();

        private async Task<List<Models.SymbolSearchResult>> Search(string pattern, string types = "class,interface,method,property", bool ignoreCase = true)
        {
            return (await _service.SearchSymbolsAsync(pattern, _builder.SolutionPath, types, ignoreCase)).ToList();
        }

        [Fact]
        public async Task SearchSymbols_FindsTypesByWildcard()
        {
            var results = await Search("Customer*", "class");

            Assert.Equal(new[] { "Customer", "CustomerService" }, results.Select(r => r.Name).OrderBy(n => n));
        }

        [Fact]
        public async Task SearchSymbols_ExactMatchIsRankedFirst()
        {
            var results = await Search("Customer*", "class");
            Assert.Equal("Customer", results[0].Name);
        }

        [Fact]
        public async Task SearchSymbols_DoesNotReturnSymbolsFromReferencedAssemblies()
        {
            // Regression: compilation.GlobalNamespace walked the whole BCL
            var results = await Search("*", "class,interface");

            Assert.All(results, r => Assert.StartsWith("Shop.", r.FullName));
            Assert.DoesNotContain(results, r => r.Name == "String");
        }

        [Fact]
        public async Task SearchSymbols_ReportsEachSymbolOnceAcrossProjectReferences()
        {
            // Regression: App references Core, so Core's symbols were found through both compilations
            var results = await Search("Customer", "class");

            var customer = Assert.Single(results);
            Assert.Equal("Core", customer.ProjectName);
        }

        [Fact]
        public async Task SearchSymbols_FindsMembersOfNestedTypes()
        {
            // Regression: recursion stopped at the first level of type members
            var results = await Search("Format*", "method");

            var method = Assert.Single(results);
            Assert.Equal("Shop.Core.Customer.Address.FormatAddress()", method.FullName);
        }

        [Fact]
        public async Task SearchSymbols_SkipsAccessorsAndImplicitMembers()
        {
            var accessors = await Search("get_*", "method");
            var constructors = await Search(".ctor", "method");

            Assert.Empty(accessors);
            Assert.Empty(constructors);
        }

        [Fact]
        public async Task SearchSymbols_FiltersBySymbolType()
        {
            var properties = await Search("*", "property");
            var fields = await Search("*", "field");

            Assert.Equal(new[] { "Age", "Name" }, properties.Select(r => r.Name).OrderBy(n => n));
            Assert.Equal("name", Assert.Single(fields).Name);
        }

        [Fact]
        public async Task SearchSymbols_UnknownSymbolTypeYieldsNothing()
        {
            Assert.Empty(await Search("*", "bogus"));
        }

        [Fact]
        public async Task SearchSymbols_RespectsCaseSensitivity()
        {
            Assert.NotEmpty(await Search("customer", "class", ignoreCase: true));
            Assert.Empty(await Search("customer", "class", ignoreCase: false));
        }

        [Fact]
        public async Task SearchSymbols_QuestionMarkMatchesSingleCharacter()
        {
            Assert.Equal("Age", Assert.Single(await Search("A?e", "property")).Name);
            Assert.Empty(await Search("A?", "property"));
        }

        [Fact]
        public async Task SearchSymbols_MatchesFullyQualifiedName()
        {
            var results = await Search("Shop.Core.I*", "interface");
            Assert.Equal("ICustomerService", Assert.Single(results).Name);
        }

        [Fact]
        public async Task SearchSymbols_FillsResultDetails()
        {
            var result = Assert.Single(await Search("Rename", "method"));

            Assert.Equal("Method", result.Category);
            Assert.Equal("internal", result.Accessibility);
            Assert.Equal("Shop.Core", result.Namespace);
            Assert.Equal("Core", result.ProjectName);
            Assert.Equal(8, result.LineNumber);
            Assert.Equal("Core/Customer.cs:8", result.Location);
            Assert.Equal("(string newName)", result.Summary);
            Assert.Equal(SymbolKind.Method, result.SymbolKind);
        }

        [Fact]
        public async Task FindReferences_IncludesDefinitionWhenAsked()
        {
            // Regression: definitions were never part of ReferencedSymbol.Locations, so the flag did nothing
            var results = (await _service.FindReferencesAsync("Rename", _builder.SolutionPath, includeDefinition: true)).ToList();

            var definition = Assert.Single(results, r => r.IsDefinition);
            Assert.EndsWith("Customer.cs", definition.DocumentPath);
            Assert.Equal(8, definition.LineNumber);

            var usage = Assert.Single(results, r => !r.IsDefinition);
            Assert.EndsWith("CustomerService.cs", usage.DocumentPath);
            Assert.Equal(8, usage.LineNumber);
            Assert.Equal("customer.Rename(name);", usage.LineText.Trim());
            Assert.Equal("Method Call", usage.ReferenceKind);
            Assert.Equal(5, usage.Context.Count);
        }

        [Fact]
        public async Task FindReferences_ExcludesDefinitionWhenNotAsked()
        {
            var results = (await _service.FindReferencesAsync("Rename", _builder.SolutionPath, includeDefinition: false)).ToList();

            Assert.Single(results);
            Assert.DoesNotContain(results, r => r.IsDefinition);
        }

        [Fact]
        public async Task FindReferences_FindsUsagesInOtherProjects()
        {
            var results = (await _service.FindReferencesAsync("CustomerService", _builder.SolutionPath, includeDefinition: false)).ToList();

            Assert.Contains(results, r => r.ProjectName == "App" && r.LineText.Contains("new CustomerService()"));
        }

        [Fact]
        public async Task FindReferences_IsCaseSensitive()
        {
            // Regression: "Name" also matched the private field "name"
            var results = (await _service.FindReferencesAsync("Name", _builder.SolutionPath, includeDefinition: true)).ToList();

            Assert.All(results, r => Assert.Equal("Name", r.SymbolName));
            Assert.Contains(results, r => r.LineText.Contains("customer.Name"));
        }

        [Fact]
        public async Task FindReferences_UnknownSymbolYieldsNothing()
        {
            Assert.Empty(await _service.FindReferencesAsync("DoesNotExist", _builder.SolutionPath, includeDefinition: true));
        }

        [Fact]
        public async Task GetSymbolInfo_DescribesMethod()
        {
            var info = await _service.GetSymbolInfoAsync("Find", _builder.SolutionPath);

            Assert.NotNull(info);
            Assert.Equal("Method", info.Kind);
            Assert.Equal("Customer", info.ReturnType);
            Assert.Equal(new[] { "string name" }, info.Parameters);
            Assert.Equal("Shop.Core", info.Namespace);
            Assert.Equal("Core", info.Assembly);
        }

        [Fact]
        public async Task GetSymbolInfo_AcceptsFullyQualifiedName()
        {
            // Regression: the tool advertises qualified names, but only simple names were matched
            var info = await _service.GetSymbolInfoAsync("Shop.Core.Customer.Address", _builder.SolutionPath);

            Assert.NotNull(info);
            Assert.Equal("Address", info.Name);
            Assert.Equal("Customer", info.DeclaringType);
            Assert.Equal("Core/Customer.cs:10", info.SourceLocation);
        }

        [Fact]
        public async Task GetSymbolInfo_UnknownSymbolReturnsNull()
        {
            Assert.Null(await _service.GetSymbolInfoAsync("DoesNotExist", _builder.SolutionPath));
        }

        [Theory]
        [InlineData("User*", "UserService", true)]
        [InlineData("User*", "MyUser", false)]
        [InlineData("*Service", "UserService", true)]
        [InlineData("Get*User", "GetActiveUser", true)]
        [InlineData("Get?ser", "GetUser", true)]
        [InlineData("Get?ser", "GetUUser", false)]
        [InlineData("A.B", "AxB", false)]
        [InlineData("a+b", "a+b", true)]
        public void WildcardRegex(string pattern, string input, bool expected)
        {
            Assert.Equal(expected, SymbolSearchService.CreateWildcardRegex(pattern, ignoreCase: false).IsMatch(input));
        }
    }
}
