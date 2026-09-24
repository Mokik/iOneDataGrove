using System.Data.Common;
using System.Text;
using System.Text.RegularExpressions;
using iOneDataGrove.Persistence.Data;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.EntityFrameworkCore;

namespace iOneDataGrove.Importer;

internal sealed partial class CSharpSymbolIndexer(IOneDataGroveDbContext dbContext)
{
    private const string ParserVersion = "csharp-syntax-v1";

    public async Task<CSharpSymbolIndexResult> IndexAsync(
        long repositoryId,
        CancellationToken cancellationToken = default)
    {
        await dbContext.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = dbContext.Database.GetDbConnection();
            var files = await ReadFilesAsync(connection, repositoryId, cancellationToken);
            var removedFiles = await RemoveStaleIndexesAsync(
                connection,
                repositoryId,
                cancellationToken);
            var filesToIndex = files
                .Where(file =>
                    !string.Equals(file.BlobSha, file.IndexedBlobSha, StringComparison.Ordinal) ||
                    !string.Equals(file.ParserVersion, ParserVersion, StringComparison.Ordinal) ||
                    file.Status is not ("indexed" or "partial"))
                .ToArray();
            var filesToIndexIds = filesToIndex
                .Select(file => file.Id)
                .ToHashSet();

            var indexedSymbols = 0;
            var partialFiles = files.Count(file =>
                !filesToIndexIds.Contains(file.Id) && file.Status == "partial");
            foreach (var file in filesToIndex)
            {
                var analysis = Analyze(file);
                await ReplaceFileIndexAsync(
                    connection,
                    repositoryId,
                    file,
                    analysis,
                    cancellationToken);
                indexedSymbols += analysis.Symbols.Count;
                if (analysis.SyntaxErrorCount > 0)
                {
                    partialFiles++;
                }
            }

            return new CSharpSymbolIndexResult(
                files.Count,
                filesToIndex.Length,
                files.Count - filesToIndex.Length,
                removedFiles,
                indexedSymbols,
                partialFiles);
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }
    }

    private static async Task<List<CSharpSourceFile>> ReadFilesAsync(
        DbConnection connection,
        long repositoryId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                source.id,
                source.path,
                source.blob_sha,
                source.content,
                file_index.blob_sha AS indexed_blob_sha,
                file_index.parser_version,
                file_index.status
            FROM github.repository_files AS source
            LEFT JOIN knowledge.code_file_indexes AS file_index
                ON file_index.repository_file_id = source.id
            WHERE source.repository_id = @repository_id
              AND NOT source.is_deleted
              AND source.extension = '.cs'
            ORDER BY source.path;
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "repository_id", repositoryId);

        var files = new List<CSharpSourceFile>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            files.Add(new CSharpSourceFile(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return files;
    }

    private static async Task<int> RemoveStaleIndexesAsync(
        DbConnection connection,
        long repositoryId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM knowledge.code_file_indexes AS file_index
            WHERE file_index.repository_id = @repository_id
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM github.repository_files AS source
                  WHERE source.id = file_index.repository_file_id
                    AND source.repository_id = @repository_id
                    AND NOT source.is_deleted
                    AND source.extension = '.cs'
              );
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "repository_id", repositoryId);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ReplaceFileIndexAsync(
        DbConnection connection,
        long repositoryId,
        CSharpSourceFile file,
        CSharpFileAnalysis analysis,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var upsertIndex = connection.CreateCommand())
            {
                upsertIndex.Transaction = transaction;
                upsertIndex.CommandText = """
                    INSERT INTO knowledge.code_file_indexes
                    (
                        repository_file_id,
                        repository_id,
                        blob_sha,
                        parser_version,
                        status,
                        symbol_count,
                        syntax_error_count,
                        indexed_at
                    )
                    VALUES
                    (
                        @repository_file_id,
                        @repository_id,
                        @blob_sha,
                        @parser_version,
                        @status,
                        @symbol_count,
                        @syntax_error_count,
                        NOW()
                    )
                    ON CONFLICT (repository_file_id) DO UPDATE SET
                        repository_id = EXCLUDED.repository_id,
                        blob_sha = EXCLUDED.blob_sha,
                        parser_version = EXCLUDED.parser_version,
                        status = EXCLUDED.status,
                        symbol_count = EXCLUDED.symbol_count,
                        syntax_error_count = EXCLUDED.syntax_error_count,
                        indexed_at = NOW();
                    """;
                AddParameter(upsertIndex, "repository_file_id", file.Id);
                AddParameter(upsertIndex, "repository_id", repositoryId);
                AddParameter(upsertIndex, "blob_sha", file.BlobSha);
                AddParameter(upsertIndex, "parser_version", ParserVersion);
                AddParameter(
                    upsertIndex,
                    "status",
                    analysis.SyntaxErrorCount == 0 ? "indexed" : "partial");
                AddParameter(upsertIndex, "symbol_count", analysis.Symbols.Count);
                AddParameter(upsertIndex, "syntax_error_count", analysis.SyntaxErrorCount);
                await upsertIndex.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var deleteSymbols = connection.CreateCommand())
            {
                deleteSymbols.Transaction = transaction;
                deleteSymbols.CommandText = """
                    DELETE FROM knowledge.code_symbols
                    WHERE repository_file_id = @repository_file_id;
                    """;
                AddParameter(deleteSymbols, "repository_file_id", file.Id);
                await deleteSymbols.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertSymbolsAsync(
                connection,
                transaction,
                repositoryId,
                file.Id,
                analysis.Symbols,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task InsertSymbolsAsync(
        DbConnection connection,
        DbTransaction transaction,
        long repositoryId,
        long repositoryFileId,
        IReadOnlyList<CSharpSymbol> symbols,
        CancellationToken cancellationToken)
    {
        const int batchSize = 200;
        foreach (var batch in symbols.Chunk(batchSize))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var sql = new StringBuilder(
                """
                INSERT INTO knowledge.code_symbols
                (
                    repository_file_id,
                    repository_id,
                    kind,
                    name,
                    qualified_name,
                    signature,
                    containing_symbol,
                    accessibility,
                    modifiers,
                    return_type,
                    start_line,
                    end_line,
                    indexed_at
                )
                VALUES
                """);

            AddParameter(command, "repository_file_id", repositoryFileId);
            AddParameter(command, "repository_id", repositoryId);

            for (var index = 0; index < batch.Length; index++)
            {
                if (index > 0)
                {
                    sql.Append(',');
                }

                var symbol = batch[index];
                sql.AppendLine();
                sql.Append(
                    $"(@repository_file_id, @repository_id, @kind_{index}, @name_{index}, " +
                    $"@qualified_name_{index}, @signature_{index}, @containing_symbol_{index}, " +
                    $"@accessibility_{index}, @modifiers_{index}, @return_type_{index}, " +
                    $"@start_line_{index}, @end_line_{index}, NOW())");

                AddParameter(command, $"kind_{index}", symbol.Kind);
                AddParameter(command, $"name_{index}", symbol.Name);
                AddParameter(command, $"qualified_name_{index}", symbol.QualifiedName);
                AddParameter(command, $"signature_{index}", symbol.Signature);
                AddParameter(command, $"containing_symbol_{index}", symbol.ContainingSymbol);
                AddParameter(command, $"accessibility_{index}", symbol.Accessibility);
                AddParameter(command, $"modifiers_{index}", symbol.Modifiers);
                AddParameter(command, $"return_type_{index}", symbol.ReturnType);
                AddParameter(command, $"start_line_{index}", symbol.StartLine);
                AddParameter(command, $"end_line_{index}", symbol.EndLine);
            }

            sql.Append(';');
            command.CommandText = sql.ToString();
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static CSharpFileAnalysis Analyze(CSharpSourceFile file)
    {
        var tree = CSharpSyntaxTree.ParseText(
            file.Content,
            CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest),
            path: file.Path);
        var root = tree.GetRoot();
        var syntaxErrors = tree.GetDiagnostics()
            .Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var symbols = new List<CSharpSymbol>();

        foreach (var node in root.DescendantNodes())
        {
            var symbol = node switch
            {
                BaseNamespaceDeclarationSyntax declaration => CreateNamespace(declaration),
                RecordDeclarationSyntax declaration => CreateType(declaration, "record"),
                ClassDeclarationSyntax declaration => CreateType(declaration, "class"),
                InterfaceDeclarationSyntax declaration => CreateType(declaration, "interface"),
                EnumDeclarationSyntax declaration => CreateType(declaration, "enum"),
                StructDeclarationSyntax declaration => CreateType(declaration, "struct"),
                MethodDeclarationSyntax declaration => CreateMethod(declaration),
                ConstructorDeclarationSyntax declaration => CreateConstructor(declaration),
                PropertyDeclarationSyntax declaration => CreateProperty(declaration),
                _ => null
            };

            if (symbol is not null)
            {
                symbols.Add(symbol);
            }
        }

        return new CSharpFileAnalysis(
            symbols
                .OrderBy(symbol => symbol.StartLine)
                .ThenBy(symbol => symbol.Kind, StringComparer.Ordinal)
                .ToArray(),
            syntaxErrors);
    }

    private static CSharpSymbol CreateNamespace(BaseNamespaceDeclarationSyntax declaration)
    {
        var parentNamespace = GetContainingNamespace(declaration);
        var name = declaration.Name.ToString();
        var qualifiedName = JoinQualified(parentNamespace, name);
        return CreateSymbol(
            declaration,
            "namespace",
            name,
            qualifiedName,
            $"namespace {name}",
            parentNamespace,
            null,
            [],
            null);
    }

    private static CSharpSymbol CreateType(BaseTypeDeclarationSyntax declaration, string kind)
    {
        var name = declaration.Identifier.ValueText;
        var containingSymbol = GetContainingTypeOrNamespace(declaration);
        var typeParameters = declaration is TypeDeclarationSyntax typeDeclaration
            ? typeDeclaration.TypeParameterList?.ToString() ?? string.Empty
            : string.Empty;
        var baseList = declaration.BaseList?.ToString() ?? string.Empty;
        var signature = NormalizeWhitespace($"{kind} {name}{typeParameters} {baseList}");
        return CreateSymbol(
            declaration,
            kind,
            name,
            JoinQualified(containingSymbol, name),
            signature,
            containingSymbol,
            GetAccessibility(declaration.Modifiers),
            GetModifiers(declaration.Modifiers),
            null);
    }

    private static CSharpSymbol CreateMethod(MethodDeclarationSyntax declaration)
    {
        var name = declaration.Identifier.ValueText;
        var containingSymbol = GetContainingTypeOrNamespace(declaration);
        var signature = NormalizeWhitespace(
            $"{declaration.ReturnType} {name}{declaration.TypeParameterList}{declaration.ParameterList}");
        return CreateSymbol(
            declaration,
            "method",
            name,
            JoinQualified(containingSymbol, name),
            signature,
            containingSymbol,
            GetAccessibility(declaration.Modifiers),
            GetModifiers(declaration.Modifiers),
            NormalizeWhitespace(declaration.ReturnType.ToString()));
    }

    private static CSharpSymbol CreateConstructor(ConstructorDeclarationSyntax declaration)
    {
        var name = declaration.Identifier.ValueText;
        var containingSymbol = GetContainingTypeOrNamespace(declaration);
        return CreateSymbol(
            declaration,
            "constructor",
            name,
            JoinQualified(containingSymbol, name),
            NormalizeWhitespace($"{name}{declaration.ParameterList}"),
            containingSymbol,
            GetAccessibility(declaration.Modifiers),
            GetModifiers(declaration.Modifiers),
            null);
    }

    private static CSharpSymbol CreateProperty(PropertyDeclarationSyntax declaration)
    {
        var name = declaration.Identifier.ValueText;
        var containingSymbol = GetContainingTypeOrNamespace(declaration);
        return CreateSymbol(
            declaration,
            "property",
            name,
            JoinQualified(containingSymbol, name),
            NormalizeWhitespace($"{declaration.Type} {name}"),
            containingSymbol,
            GetAccessibility(declaration.Modifiers),
            GetModifiers(declaration.Modifiers),
            NormalizeWhitespace(declaration.Type.ToString()));
    }

    private static CSharpSymbol CreateSymbol(
        SyntaxNode declaration,
        string kind,
        string name,
        string qualifiedName,
        string signature,
        string? containingSymbol,
        string? accessibility,
        string[] modifiers,
        string? returnType)
    {
        var lines = declaration.GetLocation().GetLineSpan();
        return new CSharpSymbol(
            kind,
            name,
            qualifiedName,
            signature,
            containingSymbol,
            accessibility,
            modifiers,
            returnType,
            lines.StartLinePosition.Line + 1,
            lines.EndLinePosition.Line + 1);
    }

    private static string? GetContainingTypeOrNamespace(SyntaxNode declaration)
    {
        var namespaceName = GetContainingNamespace(declaration);
        var types = declaration.Ancestors()
            .OfType<BaseTypeDeclarationSyntax>()
            .Reverse()
            .Select(type => type.Identifier.ValueText);
        return types.Aggregate(namespaceName, JoinQualified);
    }

    private static string? GetContainingNamespace(SyntaxNode declaration)
    {
        var namespaces = declaration.Ancestors()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .Reverse()
            .Select(item => item.Name.ToString());
        return namespaces.Aggregate<string, string?>(null, JoinQualified);
    }

    private static string JoinQualified(string? left, string right) =>
        string.IsNullOrWhiteSpace(left) ? right : $"{left}.{right}";

    private static string? GetAccessibility(SyntaxTokenList modifiers)
    {
        var hasPrivate = modifiers.Any(SyntaxKind.PrivateKeyword);
        var hasProtected = modifiers.Any(SyntaxKind.ProtectedKeyword);
        var hasInternal = modifiers.Any(SyntaxKind.InternalKeyword);
        var hasPublic = modifiers.Any(SyntaxKind.PublicKeyword);

        if (hasPrivate && hasProtected) return "private protected";
        if (hasProtected && hasInternal) return "protected internal";
        if (hasPublic) return "public";
        if (hasProtected) return "protected";
        if (hasInternal) return "internal";
        if (hasPrivate) return "private";
        return null;
    }

    private static string[] GetModifiers(SyntaxTokenList modifiers) => modifiers
        .Where(token => !token.IsKind(SyntaxKind.PublicKeyword) &&
            !token.IsKind(SyntaxKind.PrivateKeyword) &&
            !token.IsKind(SyntaxKind.ProtectedKeyword) &&
            !token.IsKind(SyntaxKind.InternalKeyword))
        .Select(token => token.ValueText)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    private static string NormalizeWhitespace(string value) =>
        WhitespaceRegex().Replace(value, " ").Trim();

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

internal sealed record CSharpSourceFile(
    long Id,
    string Path,
    string BlobSha,
    string Content,
    string? IndexedBlobSha,
    string? ParserVersion,
    string? Status);

internal sealed record CSharpFileAnalysis(
    IReadOnlyList<CSharpSymbol> Symbols,
    int SyntaxErrorCount);

internal sealed record CSharpSymbol(
    string Kind,
    string Name,
    string QualifiedName,
    string Signature,
    string? ContainingSymbol,
    string? Accessibility,
    string[] Modifiers,
    string? ReturnType,
    int StartLine,
    int EndLine);

internal sealed record CSharpSymbolIndexResult(
    int CSharpFiles,
    int IndexedFiles,
    int UnchangedFiles,
    int RemovedFiles,
    int IndexedSymbols,
    int PartialFiles);
