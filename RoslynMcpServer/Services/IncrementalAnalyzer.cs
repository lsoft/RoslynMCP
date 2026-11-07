using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcpServer.Models;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace RoslynMcpServer.Services
{
    public class FileAnalysisCache
    {
        public DateTime LastModified { get; set; }
        public string FileHash { get; set; } = string.Empty;
        public List<SymbolSearchResult> CachedSymbols { get; set; } = new();
        public List<ComplexityResult> CachedComplexity { get; set; } = new();
    }

    public class IncrementalAnalyzer
    {
        private const int ComplexityThreshold = 5;

        // Documents of a batch are analyzed concurrently, so the cache and the result are shared between threads
        private readonly ConcurrentDictionary<string, FileAnalysisCache> _fileCache;
        private readonly SemaphoreSlim _analysisLock;

        public IncrementalAnalyzer()
        {
            _fileCache = new ConcurrentDictionary<string, FileAnalysisCache>(StringComparer.OrdinalIgnoreCase);
            _analysisLock = new SemaphoreSlim(Environment.ProcessorCount);
        }
        
        public async Task<AnalysisResult> AnalyzeIncrementallyAsync(
            Solution solution, IEnumerable<DocumentId>? changedDocuments = null)
        {
            var documentsToAnalyze = changedDocuments?.ToHashSet() ?? 
                solution.Projects.SelectMany(p => p.Documents).Select(d => d.Id).ToHashSet();
            
            var result = new AnalysisResult
            {
                AnalysisStartTime = DateTime.UtcNow
            };
            
            var batchSize = Math.Max(1, Environment.ProcessorCount);
            
            // Process in batches to manage memory
            await ProcessDocumentBatches(solution, documentsToAnalyze, batchSize, result);
            
            result.AnalysisEndTime = DateTime.UtcNow;
            return result;
        }
        
        private async Task ProcessDocumentBatches(
            Solution solution, 
            HashSet<DocumentId> documentIds, 
            int batchSize, 
            AnalysisResult result)
        {
            var batches = documentIds.Batch(batchSize);
            
            foreach (var batch in batches)
            {
                await _analysisLock.WaitAsync();
                try
                {
                    var tasks = batch.Select(docId =>
                        AnalyzeDocumentAsync(solution.GetDocument(docId), result));
                    await Task.WhenAll(tasks);
                }
                finally
                {
                    _analysisLock.Release();
                }
            }
        }
        
        private async Task AnalyzeDocumentAsync(Document? document, AnalysisResult result)
        {
            if (document?.FilePath == null) return;
            
            try
            {
                // The cache is keyed by the document text, not by the file on disk: the solution
                // snapshot may hold edits that were never saved
                var sourceText = await document.GetTextAsync();
                var fileHash = ComputeHash(sourceText);

                if (_fileCache.TryGetValue(document.FilePath, out var cache) && cache.FileHash == fileHash)
                {
                    lock (result)
                    {
                        result.Symbols.AddRange(cache.CachedSymbols);
                        result.ComplexityIssues.AddRange(cache.CachedComplexity);
                    }
                    return;
                }

                var syntaxTree = await document.GetSyntaxTreeAsync();
                if (syntaxTree == null) return;

                var root = await syntaxTree.GetRootAsync();
                var semanticModel = await document.GetSemanticModelAsync();

                if (semanticModel == null) return;

                var symbols = ExtractSymbols(root, document, semanticModel);
                var complexityIssues = ComplexityCalculator.Analyze(root, document.FilePath, ComplexityThreshold);

                UpdateFileCache(document.FilePath, fileHash, symbols, complexityIssues);

                lock (result)
                {
                    result.Symbols.AddRange(symbols);
                    result.ComplexityIssues.AddRange(complexityIssues);
                    // Counts documents actually analyzed, cache hits are not included
                    result.ProcessedDocuments++;
                }
            }
            catch (Exception)
            {
                // Log error but continue processing other documents
            }
        }

        private static string ComputeHash(SourceText text)
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
        }
        
        private List<SymbolSearchResult> ExtractSymbols(SyntaxNode root, Document document, SemanticModel semanticModel)
        {
            var symbols = new List<SymbolSearchResult>();
            
            foreach (var node in root.DescendantNodes())
            {
                var symbol = semanticModel.GetDeclaredSymbol(node);
                if (symbol == null) continue;
                
                var location = node.GetLocation();
                var lineSpan = location.GetLineSpan();
                
                symbols.Add(new SymbolSearchResult
                {
                    Name = symbol.Name,
                    FullName = symbol.ToDisplayString(),
                    Category = GetSymbolCategory(symbol),
                    Location = $"{document.Project.Name}:{Path.GetFileName(document.FilePath)}:{lineSpan.StartLinePosition.Line + 1}",
                    ProjectName = document.Project.Name,
                    FilePath = document.FilePath ?? "",
                    LineNumber = lineSpan.StartLinePosition.Line + 1,
                    Summary = GetSymbolSummary(symbol),
                    Accessibility = symbol.DeclaredAccessibility.ToString().ToLower(),
                    SymbolKind = symbol.Kind,
                    Namespace = symbol.ContainingNamespace?.ToDisplayString() ?? ""
                });
            }
            
            return symbols;
        }
        
        private string GetSymbolCategory(ISymbol symbol)
        {
            return symbol switch
            {
                INamedTypeSymbol namedType => namedType.TypeKind.ToString(),
                IMethodSymbol => "Method",
                IPropertySymbol => "Property",
                IFieldSymbol => "Field",
                IEventSymbol => "Event",
                INamespaceSymbol => "Namespace",
                _ => symbol.Kind.ToString()
            };
        }
        
        private string GetSymbolSummary(ISymbol symbol)
        {
            return symbol switch
            {
                IMethodSymbol method => $"({string.Join(", ", method.Parameters.Select(p => $"{p.Type.Name} {p.Name}"))})",
                IPropertySymbol property => $": {property.Type.Name}",
                IFieldSymbol field => $": {field.Type.Name}",
                INamedTypeSymbol type => $"{type.TypeKind} with {type.GetMembers().Length} members",
                _ => ""
            };
        }
        
        private void UpdateFileCache(string filePath, string fileHash, List<SymbolSearchResult> symbols, List<ComplexityResult> complexityResults)
        {
            var cache = new FileAnalysisCache
            {
                LastModified = DateTime.UtcNow,
                FileHash = fileHash,
                CachedSymbols = symbols,
                CachedComplexity = complexityResults
            };
            
            _fileCache[filePath] = cache;
        }
    }
    
    public static class EnumerableExtensions
    {
        public static IEnumerable<IEnumerable<T>> Batch<T>(this IEnumerable<T> source, int batchSize)
        {
            var batch = new List<T>(batchSize);
            foreach (var item in source)
            {
                batch.Add(item);
                if (batch.Count == batchSize)
                {
                    yield return batch;
                    batch = new List<T>(batchSize);
                }
            }
            
            if (batch.Count > 0)
                yield return batch;
        }
    }
}
