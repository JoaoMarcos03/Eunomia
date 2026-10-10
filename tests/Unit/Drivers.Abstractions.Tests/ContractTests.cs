using System.Runtime.CompilerServices;
using Eunomia.Engine.Results;
using Eunomia.Engine.Results.Synthetic;

namespace Eunomia.Drivers.Abstractions.Tests;

public sealed class ContractTests
{
    [Theory]
    [InlineData(LogicalTypeKind.Integer, TypeCategory.Integer)]
    [InlineData(LogicalTypeKind.Boolean, TypeCategory.Integer)]
    [InlineData(LogicalTypeKind.Bit, TypeCategory.Integer)]
    [InlineData(LogicalTypeKind.Decimal, TypeCategory.Real)]
    [InlineData(LogicalTypeKind.Float, TypeCategory.Real)]
    [InlineData(LogicalTypeKind.Text, TypeCategory.Text)]
    [InlineData(LogicalTypeKind.Json, TypeCategory.Text)]
    [InlineData(LogicalTypeKind.Binary, TypeCategory.Binary)]
    [InlineData(LogicalTypeKind.Date, TypeCategory.Temporal)]
    [InlineData(LogicalTypeKind.Time, TypeCategory.Temporal)]
    [InlineData(LogicalTypeKind.DateTime, TypeCategory.Temporal)]
    [InlineData(LogicalTypeKind.Interval, TypeCategory.Temporal)]
    [InlineData(LogicalTypeKind.Spatial, TypeCategory.Spatial)]
    [InlineData(LogicalTypeKind.Enum, TypeCategory.Other)]
    [InlineData(LogicalTypeKind.Set, TypeCategory.Other)]
    [InlineData(LogicalTypeKind.Uuid, TypeCategory.Other)]
    [InlineData(LogicalTypeKind.Array, TypeCategory.Other)]
    [InlineData(LogicalTypeKind.Unknown, TypeCategory.Other)]
    public void CategoriaDeCorSegueATabela144(LogicalTypeKind kind, TypeCategory expected)
    {
        Assert.Equal(expected, new LogicalType(kind).Category);
    }

    [Fact]
    public void TamanhoDoObjetoEhDadosMaisIndices()
    {
        var table = new DbObjectRef("shop", "shop", "orders", DbObjectKind.Table);

        Assert.Equal(300, new DbObjectInfo(table, DataBytes: 200, IndexBytes: 100).SizeBytes);
        Assert.Equal(200, new DbObjectInfo(table, DataBytes: 200).SizeBytes);
        Assert.Null(new DbObjectInfo(table).SizeBytes);
    }

    /// <summary>Fake executor: shows the order of the events a driver must emit.</summary>
    private sealed class FakeExecutor(SyntheticRowSource source) : IQueryExecutor
    {
        public async IAsyncEnumerable<ResultEvent> ExecuteAsync(SqlBatch batch, ExecuteOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (var s = 0; s < batch.Statements.Count; s++)
            {
                yield return new ResultSetStarted(s, 0, source.Schema);
                var blocks = source.BlockCount();
                for (var b = 0; b < blocks; b++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return new RowBlockReceived(s, 0, await source.GetBlockAsync(b, cancellationToken));
                }
                yield return new ResultSetCompleted(s, 0, source.RowCount.Count);
                yield return new StatementCompleted(s, 0, source.RowCount.Count, 0, TimeSpan.FromMilliseconds(1), TimeSpan.Zero);
            }
        }
    }

    [Fact]
    public async Task StreamDeResultadosEntregaBlocosSemMaterializar()
    {
        var executor = new FakeExecutor(new SyntheticRowSource(rows: 2500, columns: 5));
        var ct = TestContext.Current.CancellationToken;
        long rows = 0;
        var kinds = new List<string>();

        await foreach (var e in executor.ExecuteAsync(SqlBatch.Single("SELECT 1"), new ExecuteOptions(), ct))
        {
            kinds.Add(e.GetType().Name);
            if (e is RowBlockReceived r)
            {
                rows += r.Block.RowCount;
            }
        }

        Assert.Equal(2500, rows);
        Assert.Equal(
            ["ResultSetStarted", "RowBlockReceived", "RowBlockReceived", "RowBlockReceived", "ResultSetCompleted", "StatementCompleted"],
            kinds);
    }
}
